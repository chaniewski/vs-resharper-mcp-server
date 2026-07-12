using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.Application;
using JetBrains.Application.Parts;
using JetBrains.Application.StdApplicationUI.StatusBars;
using JetBrains.Application.Threading;
using JetBrains.Application.UI.Controls;
using JetBrains.Lifetimes;
using JetBrains.Util;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using XC.VsResharperMcpServer.Core.Tools;

namespace XC.VsResharperMcpServer.Host
{
    // Process-wide singleton. Starts the embedded MCP HTTP server. No true "primary" designation
    // exists - every instance independently tries the same base port, then falls back sequentially
    // (23741, 23742, ...) if it's taken, so with N concurrent devenv.exe instances each one ends up
    // as a fully independent, fully functional MCP server on its own port. Confirmed working via a
    // real 2-instance test (see docs/DEVNOTES.md "M6 multi-instance" entry): no cross-instance leakage,
    // each server's tools operate only on that instance's own solution.
    //
    // Port binding is LAZY, triggered by McpServerComponent (EnsureStarted) once a solution is known
    // to open - not eagerly here at shell-construction time - so the first solution to open in this
    // process gets a chance to bias which port gets tried first, via SolutionPortPreferences. Once
    // bound (whichever solution triggers it first), the port is fixed for the rest of this process's
    // lifetime; a second solution opening later in the same process (close A, open B) just inherits
    // whatever port is already bound, but still gets its own preference file updated to that port for
    // next time it opens fresh.
    //
    // Writes a marker file instead of relying solely on the ReSharper log, because ReSharper's
    // default log verbosity can suppress Info-level messages entirely - a marker file gives an
    // unambiguous, log-level-independent yes/no signal. One file PER INSTANCE (keyed by bound port),
    // not a single shared path - an earlier single-shared-path design got silently overwritten by
    // whichever instance loaded most recently, making it useless for telling instances apart. The
    // marker directory as a whole is a live, file-per-running-instance registry: every currently
    // running instance's port + (once a solution is open) solution path, cleaned up when that
    // instance's VS process closes. Also backs the list_solutions tool registered below.
    [ShellComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class McpShellComponent
    {
        private const int DefaultPort = 23741;
        private const int MaxPortAttempts = 10;

        public static readonly string MarkerDirectory =
            Path.Combine(Path.GetTempPath(), "XC.VsResharperMcpServer");

        // Read from the assembly (set via <Version> in XC.VsResharperMcpServer.csproj) rather than hardcoding
        // it here, so it can never silently go stale relative to what's actually installed.
        public static readonly string PluginVersion =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        // Always non-null from construction, independent of whether the HTTP server has bound a
        // port yet - it's just a data structure that McpServerComponent adds/removes tools into.
        public McpServerPrimitiveCollection<McpServerTool> ToolCollection { get; }

        // -1 until EnsureStarted successfully binds a port (or forever, if every port in range was
        // unavailable).
        public int Port { get; private set; } = -1;

        private readonly Lifetime _lifetime;
        private readonly ILogger _logger;
        private readonly IShellLocks _shellLocks;
        private readonly IStatusBar _statusBar;
        private readonly IStatusBarProgressIndicatorContentAutomationProvider _statusBarContentProvider;
        private readonly object _startLock = new object();
        private string _markerFilePath;
        private string _loadedMessage;
        private string _solutionPath;
        private string _statusBarDiagnostic = "(not yet attempted)";
        private StatusBarProgressIndicator _portIndicator;

        public McpShellComponent(Lifetime lifetime, ILogger logger, IShellLocks shellLocks, IStatusBar statusBar,
            IStatusBarProgressIndicatorContentAutomationProvider statusBarContentProvider)
        {
            _lifetime = lifetime;
            _logger = logger;
            _shellLocks = shellLocks;
            _statusBar = statusBar;
            _statusBarContentProvider = statusBarContentProvider;

            _loadedMessage = "XC.VsResharperMcpServer: McpShellComponent constructed - plugin loaded successfully. Version: " + PluginVersion;
            logger.Info(_loadedMessage);

            var options = BuildServerOptions();
            ToolCollection = options.ToolCollection;

            ToolCollection.Add(McpServerTool.Create((Func<string>)(() => new ListSolutionsTool(this).Execute()),
                new McpServerToolCreateOptions
                {
                    Name = "list_solutions",
                    Description = "Lists every currently running xc-vs-resharper-mcp-server instance on this machine " +
                        "(port, process id, and open solution path for each), read from a live per-instance " +
                        "marker-file registry - not just the instance this tool call happens to be answered " +
                        "by. Useful when multiple Visual Studio + ReSharper windows are open at once and you " +
                        "need to figure out which port corresponds to which solution, e.g. when adding another " +
                        "server entry to .mcp.json."
                }));
        }

        // Idempotent - the first caller (whichever solution opens first in this process) wins; later
        // calls just return the already-bound port (or -1), ignoring their own preferredPort. Safe to
        // call from multiple SolutionComponents' constructors without double-binding.
        public int EnsureStarted(int? preferredPort)
        {
            lock (_startLock)
            {
                if (Port > 0 || _started)
                    return Port;
                _started = true;

                var candidates = BuildCandidatePorts(preferredPort);
                McpHttpServer server = null;
                Exception lastError = null;

                foreach (var tryPort in candidates)
                {
                    try
                    {
                        var tryServer = new McpHttpServer(tryPort, _logger, ExistingOptions);
                        tryServer.Start();
                        server = tryServer;
                        Port = tryPort;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        _logger.Info("XC.VsResharperMcpServer: port " + tryPort + " unavailable (" + ex.Message + "), trying next...");
                    }
                }

                if (server != null)
                {
                    _lifetime.OnTermination(server);
                    _logger.Info("XC.VsResharperMcpServer: MCP server started on port " + Port +
                        (preferredPort.HasValue && preferredPort.Value == Port ? " (matched this solution's remembered port)" : ""));
                    ShowPortIndicator(Port);
                }
                else if (lastError != null)
                {
                    _logger.Error(lastError, "XC.VsResharperMcpServer: failed to bind any port among: " + string.Join(", ", candidates));
                }

                WriteMarkerFile();

                // Best-effort: remove this instance's marker file when VS closes, so the marker
                // directory only ever lists currently-running instances, not stale leftovers.
                _lifetime.OnTermination(() =>
                {
                    try { if (_markerFilePath != null && File.Exists(_markerFilePath)) File.Delete(_markerFilePath); }
                    catch { /* best-effort */ }
                });

                return Port;
            }
        }

        private bool _started;

        private static int[] BuildCandidatePorts(int? preferredPort)
        {
            var basePort = GetConfiguredPort();
            var range = Enumerable.Range(basePort, MaxPortAttempts);
            if (!preferredPort.HasValue || preferredPort.Value <= 0)
                return range.ToArray();

            // Try the remembered port first, then fall back to the normal range (deduplicated) -
            // covers the common case (it's free, instant win) and the case where it's now taken by
            // a different instance (falls through to the same behavior as if there were no
            // preference at all).
            return new[] { preferredPort.Value }.Concat(range).Distinct().ToArray();
        }

        // BuildServerOptions() is only ever called once (from the constructor); EnsureStarted reuses
        // the same options/ToolCollection instance rather than rebuilding it, so tools added before
        // the port bound (like list_solutions above) aren't lost.
        private McpServerOptions ExistingOptions => _cachedOptions;
        private McpServerOptions _cachedOptions;

        // Read-only VS status bar indicator showing the bound port. Two earlier attempts both
        // called into IStatusBar synchronously, inline, from wherever EnsureStarted happened to run
        // (McpServerComponent's constructor) - both compiled clean, threw no exception, and rendered
        // nothing visible in a live install (see docs/DEVNOTES.md "M6 status bar" entries). Decompiled
        // ReSharper's own ExceptionStatusBarIndicator (confirmed-visible, real users see this) and
        // found it does NOT create its indicator inline either - it defers via
        // Lifetime.Start(invocator.Tasks.UnguardedMainThreadScheduler, action). Since IShellLocks -
        // already proven reliable elsewhere in this codebase (PsiThreadDispatcher) - provides the
        // same "queue this for proper-thread execution later" primitive via ExecuteOrQueue, using
        // that here instead of chasing IThreading's exact type signature (which reflection couldn't
        // pin down cleanly this round). If the indicator still doesn't render after this change, that
        // would be real evidence the problem is NOT timing/thread-affinity (ruling out the strongest
        // current hypothesis) and points instead at getting a fundamentally wrong/disconnected
        // IStatusBar instance (see the StatusBarExt backend/frontend RD-split finding in DEVNOTES).
        private void ShowPortIndicator(int port)
        {
            _shellLocks.ExecuteOrQueue("XC.VsResharperMcpServer.ShowPortIndicator", () =>
            {
                try
                {
                    _portIndicator = new StatusBarProgressIndicator(
                        _lifetime, _statusBar, _statusBarContentProvider, icon: McpProtocolIcon.Instance, text: "R# MCP: " + port);
                    _statusBarDiagnostic = "created OK, IsVisible=" + _statusBar.IsVisible.Value;
                    _logger.Info("XC.VsResharperMcpServer: status bar indicator created for port " + port +
                        " (IsVisible=" + _statusBar.IsVisible.Value + ")");
                }
                catch (Exception ex)
                {
                    _statusBarDiagnostic = "FAILED: " + ex;
                    _logger.Error(ex, "XC.VsResharperMcpServer: failed to create/update status bar indicator");
                }

                WriteMarkerFile();
            });
        }

        // Called by McpServerComponent once a solution is open, so the marker file can report
        // which solution this port is actually serving - the shell component alone never knows
        // this (it constructs before any solution opens). Also called with null on solution close,
        // so the marker file correctly reflects "no solution open" rather than a stale path.
        public void UpdateMarkerFileSolution(string solutionPath)
        {
            if (Port <= 0) return;
            _solutionPath = solutionPath;
            WriteMarkerFile();
        }

        private static int GetConfiguredPort()
        {
            var env = Environment.GetEnvironmentVariable("XC_VSRESHARPERMCPSERVER_PORT");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var parsed))
                return parsed;
            return DefaultPort;
        }

        // Sent to every connecting client during the initialize handshake (MCP's ServerInstructions
        // field - clients typically feed this to the LLM as system-message context). This is the
        // one place that reaches every agent regardless of which specific tool descriptions it reads,
        // which is why the sync_file_from_disk requirement lives here rather than only in that tool's
        // own description - see docs/DEVNOTES.md for the staleness investigation this addresses.
        private const string ServerInstructions =
            "This server exposes ReSharper's code intelligence (find usages, rename, diagnostics, " +
            "refactorings, etc.) for whatever solution is currently open in this Visual Studio " +
            "instance.\n\n" +
            "IMPORTANT WORKFLOW REQUIREMENT: if you edit a file directly through your own file-editing " +
            "tools (i.e. NOT via one of this server's own write tools), you MUST call " +
            "sync_file_from_disk on that file - or on all touched files in one call via its " +
            "'filePaths' array - before calling any other tool against it. ReSharper's own file-change " +
            "tracker is suspended for an unbounded time whenever the Visual Studio window is not the " +
            "active/focused window, which is true for essentially every automated or headless session. " +
            "Without an explicit sync_file_from_disk call, every other tool here may silently operate " +
            "on stale, pre-edit content - reads will report outdated information and writes (rename, " +
            "generate_members, etc.) risk clobbering your edits with a stale cached version. Tools you " +
            "call on this server itself (rename_symbol, generate_members, format_file, ...) always stay " +
            "in sync automatically and never need this extra step - it is only needed after edits made " +
            "outside this server.\n\n" +
            "POSITION SEMANTICS: for every tool taking a line/column position, the caret sits in the gap " +
            "BETWEEN characters, not on a character. Pointing at an identifier's first character can fail " +
            "to resolve ('No resolvable symbol found') while pointing at its last character (or the " +
            "position immediately after it) resolves correctly. If a position-based call unexpectedly " +
            "fails to resolve a symbol you can see is right there, try the end of the identifier before " +
            "concluding the tool is broken.";

        private McpServerOptions BuildServerOptions()
        {
            _cachedOptions = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "xc-vs-resharper-mcp-server", Version = PluginVersion },
                ServerInstructions = ServerInstructions,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal)
            };
            return _cachedOptions;
        }

        // Writes/overwrites this instance's marker file from current instance state (Port,
        // _solutionPath, _statusBarDiagnostic) - called from every site that changes any of that
        // state (port bound, solution opened/closed, status bar indicator attempted), so the file on
        // disk always reflects the latest known state rather than a snapshot from whichever call
        // happened to run first. StatusBarDiagnostic in particular exists because ReSharper's own log
        // has proven hard to reliably access this session - this is the one channel confirmed to work.
        private void WriteMarkerFile()
        {
            try
            {
                Directory.CreateDirectory(MarkerDirectory);

                var portInfo = Port > 0
                    ? "HTTP server bound to port: " + Port
                    : "HTTP server FAILED to bind (see ReSharper log for details)";

                // Keyed by port, not a single shared filename - each concurrently running instance
                // gets its own file, so the directory as a whole lists every live instance.
                _markerFilePath = Port > 0
                    ? Path.Combine(MarkerDirectory, "instance-port-" + Port + ".txt")
                    : Path.Combine(MarkerDirectory, "instance-pid-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-failed.txt");

                var header = _loadedMessage ?? ("XC.VsResharperMcpServer: instance " + System.Diagnostics.Process.GetCurrentProcess().Id + " (updated)");

                File.WriteAllText(_markerFilePath,
                    header + Environment.NewLine +
                    portInfo + Environment.NewLine +
                    "PID: " + System.Diagnostics.Process.GetCurrentProcess().Id + Environment.NewLine +
                    "Solution: " + (_solutionPath ?? "(none open)") + Environment.NewLine +
                    "StatusBarIndicator: " + _statusBarDiagnostic + Environment.NewLine +
                    "Timestamp: " + DateTime.Now.ToString("O"),
                    new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "XC.VsResharperMcpServer: failed to write marker file in " + MarkerDirectory);
            }
        }
    }
}
