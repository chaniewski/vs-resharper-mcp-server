using System;
using System.IO;
using System.Linq;
using System.Text;
using XC.VsResharperMcpServer.Host;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M6. Reads McpShellComponent's marker-file directory (see docs/DEVNOTES.md) to report
    // every currently running xc-vs-resharper-mcp-server instance on this machine - not just the one this call
    // happens to be answered by. Registered directly by McpShellComponent (not McpServerComponent),
    // since it's a process-wide/machine-wide capability independent of any one solution - it should
    // work even before a solution is open.
    public class ListSolutionsTool
    {
        private readonly McpShellComponent _shellComponent;

        public ListSolutionsTool(McpShellComponent shellComponent)
        {
            _shellComponent = shellComponent;
        }

        public string Execute()
        {
            try
            {
                if (!Directory.Exists(McpShellComponent.MarkerDirectory))
                    return "No xc-vs-resharper-mcp-server instances found (marker directory does not exist).";

                var files = Directory.GetFiles(McpShellComponent.MarkerDirectory, "instance-*.txt")
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count == 0)
                    return "No xc-vs-resharper-mcp-server instances found.";

                var sb = new StringBuilder();
                sb.Append(files.Count).AppendLine(" xc-vs-resharper-mcp-server instance(s) found:");
                foreach (var file in files)
                {
                    sb.AppendLine();
                    sb.Append(Describe(file));
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "Could not read instance registry: " + ex.Message;
            }
        }

        private string Describe(string markerFile)
        {
            try
            {
                var lines = File.ReadAllLines(markerFile);
                var port = Find(lines, "HTTP server bound to port: ");
                var pid = Find(lines, "PID: ");
                var solution = Find(lines, "Solution: ");
                var timestamp = Find(lines, "Timestamp: ");

                var isSelf = int.TryParse(port, out var portNum) && portNum == _shellComponent.Port;

                var sb = new StringBuilder();
                sb.Append("  port ").Append(port ?? "(unknown)");
                if (isSelf) sb.Append(" (this instance)");
                sb.AppendLine();
                if (pid != null) sb.Append("    pid: ").AppendLine(pid);
                sb.Append("    solution: ").AppendLine(solution ?? "(none open)");
                if (timestamp != null) sb.Append("    last updated: ").AppendLine(timestamp);
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "  (could not read " + Path.GetFileName(markerFile) + ": " + ex.Message + ")";
            }
        }

        private static string Find(string[] lines, string prefix)
        {
            foreach (var line in lines)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return line.Substring(prefix.Length).Trim();
            }
            return null;
        }
    }
}
