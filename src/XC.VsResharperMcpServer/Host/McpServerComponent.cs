using System;
using System.Collections.Generic;
using JetBrains.Application.DataContext;
using JetBrains.Application.Parts;
using JetBrains.Application.Threading;
using JetBrains.DocumentManagers;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCleanup;
using JetBrains.TextControl;
using JetBrains.Util;
using ModelContextProtocol.Server;
using XC.VsResharperMcpServer.Core.Tools;

namespace XC.VsResharperMcpServer.Host
{
    // Per-solution. Constructs ISolution-bound tool instances and adds them to the single
    // process-wide MCP server's ToolCollection (owned by McpShellComponent) on solution open;
    // removes them on solution close. 27 tools total on this branch: 15 read (M2+M3) + 7 write (M4) +
    // sync_file_from_disk + list_solutions + 2 refactorings beyond rename (M7: inline_variable,
    // change_signature) + structural_search (M8 spike - search only, NOT LIVE-TESTED, see
    // docs/DEVNOTES.md). Branched from main before extract_method/move_type/fix_usings-scope/
    // generate_xml_doc/code_metrics (sibling M7/M9/M10 branches) - none of these are merged together
    // yet. safe_delete was implemented then dropped - see docs/DEVNOTES.md "safe_delete dropped" entry.
    //
    // Also where the HTTP server's port actually gets bound (McpShellComponent.EnsureStarted) -
    // deliberately deferred to here (not eager at shell-construction time) so this solution's
    // remembered port preference (SolutionPortPreferences) can bias which port gets tried first.
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class McpServerComponent : IDisposable
    {
        private readonly McpServerPrimitiveCollection<McpServerTool> _toolCollection;
        private readonly McpServerTool[] _registeredTools;
        private readonly McpShellComponent _shellComponent;
        private readonly ILogger _logger;

        public McpServerComponent(
            Lifetime lifetime,
            ISolution solution,
            IShellLocks shellLocks,
            McpShellComponent shellComponent,
            CodeCleanupSettingsComponent cleanupSettings,
            DataContexts dataContexts,
            ITextControlManager textControlManager,
            DocumentManager documentManager,
            ILogger logger)
        {
            _logger = logger;
            _shellComponent = shellComponent;
            _toolCollection = shellComponent.ToolCollection;

            var solutionPath = solution.SolutionFilePath?.FullPath;
            int? preferredPort = null;
            if (SolutionPortPreferences.TryGetPreferredPort(solutionPath, out var remembered))
                preferredPort = remembered;

            var boundPort = shellComponent.EnsureStarted(preferredPort);

            if (boundPort <= 0)
            {
                logger.Info("XC.VsResharperMcpServer: MCP HTTP server did not start; skipping tool registration for this solution.");
                lifetime.OnTermination(this);
                return;
            }

            SolutionPortPreferences.SavePreferredPort(solutionPath, boundPort);

            var findUsages = new FindUsagesTool(solution, shellLocks);
            var goToDefinition = new GoToDefinitionTool(solution, shellLocks);
            var getDiagnostics = new GetDiagnosticsTool(solution, shellLocks);
            var searchSymbol = new SearchSymbolTool(solution, shellLocks);
            var getSolutionStructure = new GetSolutionStructureTool(solution, shellLocks);
            var getSymbolInfo = new GetSymbolInfoTool(solution, shellLocks);
            var findImplementations = new FindImplementationsTool(solution, shellLocks);
            var getFileErrors = new GetFileErrorsTool(solution, shellLocks);
            var browseNamespace = new BrowseNamespaceTool(solution, shellLocks);
            var listSymbolsInFile = new ListSymbolsInFileTool(solution, shellLocks);
            var getSymbolSource = new GetSymbolSourceTool(solution, shellLocks);
            var getCallHierarchy = new GetCallHierarchyTool(solution, shellLocks);
            var getTypeHierarchy = new GetTypeHierarchyTool(solution, shellLocks);
            var listQuickFixes = new ListQuickFixesTool(solution, shellLocks);
            var flow = new FlowTool(solution, shellLocks);
            var renameSymbol = new RenameSymbolTool(solution, shellLocks);
            var generateMembers = new GenerateMembersTool(solution, shellLocks);
            var fixUsings = new FixUsingsTool(solution, shellLocks);
            var formatFile = new FormatFileTool(solution, shellLocks, cleanupSettings);
            var applyQuickFix = new ApplyQuickFixTool(solution, shellLocks);
            var applySuggestions = new ApplySuggestionsTool(solution, shellLocks);
            var completeAt = new CompleteAtTool(solution, shellLocks);
            var syncFileFromDisk = new SyncFileFromDiskTool(solution, shellLocks);
            var inlineVariable = new InlineVariableTool(solution, shellLocks, dataContexts, textControlManager);
            var changeSignature = new ChangeSignatureTool(solution, shellLocks);
            var structuralSearch = new StructuralSearchTool(solution, shellLocks, documentManager);

            _registeredTools = new[]
            {
                McpServerTool.Create((Func<string, string, string, int, int, bool, int, string>)findUsages.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "find_usages",
                        Description = "Find all usages/references of a code symbol (class, method, property, variable, etc.) " +
                            "in the current solution. Provide either a symbolName (e.g. 'MyClass' or 'Namespace.MyClass') " +
                            "or a file path with position (line/column)."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, int, string>)goToDefinition.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "go_to_definition",
                        Description = "Navigate to the definition/declaration of a symbol. Given a usage site " +
                            "(file+line+column) or a symbol name, returns the file path and position where the " +
                            "symbol is declared, along with the declaration source text."
                    }),
                McpServerTool.Create((Func<string, string, int, int, string>)getDiagnostics.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_diagnostics",
                        Description = "Run ReSharper's daemon inspections on a file and report diagnostics: " +
                            "severity, inspection id, message, location, and whether a quick-fix is available. " +
                            "Filter with minSeverity ('error'|'warning'|'suggestion'|'hint', default 'warning')."
                    }),
                McpServerTool.Create((Func<string, string, bool, int, string>)searchSymbol.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "search_symbol",
                        Description = "Search for symbols (types, methods, properties, etc.) by name across the " +
                            "entire solution. Supports partial/substring matching. Dot-qualified queries like " +
                            "'IProfile.Fake' match members by ContainingType.MemberName."
                    }),
                McpServerTool.Create((Func<bool, string>)getSolutionStructure.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_solution_structure",
                        Description = "Get the solution structure: all projects with their paths, target " +
                            "frameworks, and project-to-project references."
                    }),
                McpServerTool.Create((Func<string, string, bool, string, int, int, string>)getSymbolInfo.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_symbol_info",
                        Description = "Get detailed information about a code symbol: kind, full qualified name, " +
                            "type, documentation, containing type/namespace, and parameter info for methods. " +
                            "Provide either a symbolName or a file path with position."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, int, string>)findImplementations.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "find_implementations",
                        Description = "Find all implementations of an interface, abstract class, or virtual/abstract " +
                            "member. Returns the locations of all concrete implementations in the solution, " +
                            "distinguishing direct implementations from indirect ones (via intermediate interfaces)."
                    }),
                McpServerTool.Create((Func<string, string>)getFileErrors.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_file_errors",
                        Description = "Get compile errors and unresolved references in a file by walking the PSI " +
                            "tree. Returns error elements with their location and description."
                    }),
                McpServerTool.Create((Func<string, string>)browseNamespace.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "browse_namespace",
                        Description = "Browse the namespace hierarchy. With no arguments, lists all top-level " +
                            "namespaces. With a namespace name, lists its child namespaces and types."
                    }),
                McpServerTool.Create((Func<string, string, bool, string>)listSymbolsInFile.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "list_symbols_in_file",
                        Description = "List all symbols declared in a file: types, methods, properties, fields, " +
                            "events. Provides a structural overview of a file without reading the full source."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, bool, string>)getSymbolSource.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_symbol_source",
                        Description = "Get the FULL declaration source code of a symbol (class, method, property, " +
                            "etc.), not just a short snippet. By default returns only the primary declaration; " +
                            "set allDeclarations=true to return every partial declaration/overload."
                    }),
                McpServerTool.Create((Func<string, string, string, string, int, int, int, string>)getCallHierarchy.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_call_hierarchy",
                        Description = "Build a call hierarchy tree for a method/function. 'incoming' finds callers " +
                            "(who calls this method, recursively); 'outgoing' finds callees (which methods this " +
                            "method calls, recursively). Bounded by maxDepth (default 2, capped at 4)."
                    }),
                McpServerTool.Create((Func<string, string, string, string, int, int, int, string>)getTypeHierarchy.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "get_type_hierarchy",
                        Description = "Get the inheritance hierarchy of a type as a tree. Direction 'supertypes' " +
                            "walks up to base classes and implemented interfaces; direction 'subtypes' walks down " +
                            "to derived classes and implementors. Use maxDepth to bound recursion (default 3)."
                    }),
                McpServerTool.Create((Func<string, int, int, string>)listQuickFixes.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "list_quick_fixes",
                        Description = "List the ReSharper quick-fixes (bulb actions) available at a position in a " +
                            "file. Runs the daemon inspections over the file and reports the available quick-fixes: " +
                            "the fix id, the bulb-action display text, and the quick-fix/highlighting .NET types."
                    }),
                McpServerTool.Create((Func<string, string, int, bool, string, int, int, string>)flow.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "flow",
                        Description = "Describe the control flow of a method or type: ordered execution steps, " +
                            "branch conditions, loops, error paths (try/catch, guard clauses), inlined call " +
                            "targets, and why-hints from comments and variable names. For methods: a narrated " +
                            "control-flow summary. For types: describes all non-trivial methods. Use depth to " +
                            "control how many call levels get inlined (default 2)."
                    }),
                McpServerTool.Create((Func<string, string, string, string, int, int, bool, string>)renameSymbol.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "rename_symbol",
                        Description = "WRITE: safely rename a code symbol (class, method, property, field, " +
                            "parameter, local, etc.) and all of its references across the entire solution. " +
                            "Semantic rename, not a text match. Set dryRun=true to preview conflicts and " +
                            "affected files without modifying any code."
                    }),
                McpServerTool.Create((Func<string, string, string[], string, int, int, string>)generateMembers.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "generate_members",
                        Description = "WRITE: generate members on a type. kind='constructor' generates a " +
                            "constructor initializing the type's fields/settable properties; " +
                            "kind='equality-members' generates Equals(T)/Equals(object)/GetHashCode. " +
                            "('implement-interface'/'override-members' are accepted but not yet supported.)"
                    }),
                McpServerTool.Create((Func<string, Dictionary<string, string>, string>)fixUsings.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "fix_usings",
                        Description = "WRITE: fix missing using directives in a C# file. Finds unresolved type " +
                            "references and adds using directives for unambiguous matches. Reports ambiguous " +
                            "matches with candidates; pass 'resolutions' (type name -> namespace) to resolve them."
                    }),
                McpServerTool.Create((Func<string, string, string>)formatFile.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "format_file",
                        Description = "WRITE: format a source file or run full code cleanup. mode='format' " +
                            "(default): indentation/spacing/line breaks. mode='cleanup': full cleanup (formatting " +
                            "plus code style: redundant qualifiers, using order, var preferences, naming). " +
                            "mode='style': code style fixes only, no reformatting."
                    }),
                McpServerTool.Create((Func<string, int, int, string, int, string>)applyQuickFix.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "apply_quick_fix",
                        Description = "WRITE, RISKY/best-effort: apply a ReSharper quick-fix (bulb action) at a " +
                            "position. Omit fixId/index to list the available fixes at that position. If exactly " +
                            "one fix is available it is applied automatically. May not succeed headlessly since " +
                            "quick-fix execution normally needs an interactive editor."
                    }),
                McpServerTool.Create((Func<string, string, bool, bool, string>)applySuggestions.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "apply_suggestions",
                        Description = "WRITE: apply ReSharper suggestion quick-fixes across a whole file by " +
                            "inspection id (position-free, file-wide - complements apply_quick_fix). Specify " +
                            "'inspectionIds' or pass all=true. Only headlessly-applicable (scoped) fixes are " +
                            "applied; others are reported as skipped. Pass dryRun=true to preview."
                    }),
                McpServerTool.Create((Func<string, int, int, int, string>)completeAt.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "complete_at",
                        Description = "RISKY/best-effort, read-only: get code completion suggestions at a " +
                            "position (LSP textDocument/completion parity). May return an empty list when run " +
                            "outside an interactive editing session."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, bool, string>)inlineVariable.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "inline_variable",
                        Description = "WRITE: inline a local variable/constant - replace every read of it with " +
                            "its initializer expression and remove the declaration. The inverse of extracting a " +
                            "local. Provide either a symbolName or a file path with position. Set dryRun=true to " +
                            "preview conflicts without applying anything."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, int[], string[], string, bool, string>)changeSignature.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "change_signature",
                        Description = "WRITE: reorder, remove, or retype the parameters of a method/constructor/" +
                            "indexer, and/or change its return type, updating every call site. Provide either a " +
                            "symbolName or a file path with position. 'parameterOrder' lists ORIGINAL parameter " +
                            "indices (0-based, as in the current signature) in the desired final order; omit an " +
                            "index to remove that parameter. 'parameterTypes', if given, must be the same length " +
                            "as parameterOrder and gives a new type for each corresponding kept parameter (empty " +
                            "string/null entry = keep its current type). 'newReturnType' changes the return type. " +
                            "Adding brand-new parameters is not yet supported; to rename a parameter use " +
                            "rename_symbol instead. Set dryRun=true to preview conflicts and affected files " +
                            "without modifying any code. CAUTION: removing a parameter that is still referenced " +
                            "in the method body is NOT reported as a conflict and will leave a compile error " +
                            "there - after removing a parameter, check the method body yourself (e.g. via " +
                            "get_diagnostics/get_file_errors) rather than trusting a clean '(applied)' result."
                    }),
                McpServerTool.Create((Func<string, string, int, string>)structuralSearch.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "structural_search",
                        Description = "READ-ONLY M8 SPIKE, NOT LIVE-TESTED YET (see docs/DEVNOTES.md): search " +
                            "for code matching a ReSharper Structural Search pattern (AST-structural, not text/" +
                            "regex - e.g. \"$expr$.ToString()\" matches any ToString() call regardless of " +
                            "formatting/receiver expression complexity; $name$ placeholders match any " +
                            "sub-element). Omit 'filePath' to search the whole solution, or scope to one file. " +
                            "CAUTION: the SDK wiring is confirmed real (every type/constructor used is read " +
                            "directly from decompiled source), but whether any given pattern string actually " +
                            "parses and matches correctly is completely unverified without live testing - " +
                            "unlike a compile error, a wrong-but-parseable pattern will just silently return " +
                            "the wrong (possibly empty, possibly everything) result set. No replace - search " +
                            "only, per the M8 spike's own scope."
                    }),
                McpServerTool.Create((Func<string, string[], string>)syncFileFromDisk.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "sync_file_from_disk",
                        Description = "WRITE, IMPORTANT WORKFLOW STEP: force ReSharper's PSI to resync one or more " +
                            "files' content from disk right now, bypassing the platform's own file-change tracker " +
                            "(which is suspended for an unbounded time while the VS window is inactive/minimized - " +
                            "true for essentially every automated/headless session). Call this immediately after " +
                            "editing any file directly on the filesystem (i.e. NOT through this MCP server's own " +
                            "write tools - through your own file-editing tools) and BEFORE using any other tool " +
                            "against that file. Skipping this step means every other tool in this server " +
                            "(find_usages, rename_symbol, get_diagnostics, etc.) may silently operate on stale, " +
                            "pre-edit content. Accepts a single 'filePath' or a 'filePaths' array to sync several " +
                            "files (e.g. every file touched by a multi-file edit) in one call."
                    })
            };

            foreach (var tool in _registeredTools)
                _toolCollection.Add(tool);

            logger.Info("XC.VsResharperMcpServer: registered " + _registeredTools.Length + " tools for solution " +
                (solution.SolutionFilePath?.FullPath ?? "(unknown)"));

            shellComponent.UpdateMarkerFileSolution(solution.SolutionFilePath?.FullPath);

            lifetime.OnTermination(this);
        }

        public void Dispose()
        {
            if (_toolCollection == null || _registeredTools == null)
                return;

            foreach (var tool in _registeredTools)
                _toolCollection.Remove(tool);

            _shellComponent.UpdateMarkerFileSolution(null);

            _logger.Info("XC.VsResharperMcpServer: unregistered tools for closed solution.");
        }
    }
}
