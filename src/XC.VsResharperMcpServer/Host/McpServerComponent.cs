using System;
using System.Collections.Generic;
using JetBrains.Application.DataContext;
using JetBrains.Application.FileSystemTracker;
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
    // removes them on solution close. 29 tools REGISTERED (down from 32 - see below) + fix_usings'
    // project/solution scope extension (M9, same tool, not a new registration). All registered tools
    // confirmed live-tested and working as of 2026-07-12 - see docs/DEVNOTES.md, including the
    // CompilationContextCookie fix (in PsiThreadDispatcher, applies to every tool) that a real
    // extract_method SDK NullReferenceException led to.
    //
    // Two tools DISABLED 2026-07-12 (see docs/DEVNOTES.md "flaky and low-value tools" review) -
    // commented out below rather than deleted, so re-enabling later is a small diff, not a rewrite:
    // get_file_errors (redundant with, and less reliable than, get_diagnostics), complete_at
    // (structurally can never return a useful result headlessly, and near-zero agent value even if it
    // could). structural_search was disabled the same day for hanging the same PSI-lock-wedge way
    // apply_quick_fix did, then RE-ENABLED after the hang was root-caused and fixed (see the tool's
    // own registration comment below and docs/DEVNOTES.md). One tool DROPPED entirely (not disabled -
    // no working implementation to keep): safe_delete - see docs/DEVNOTES.md "safe_delete dropped" entry.
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
            IFileSystemTracker fileSystemTracker,
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
            var syncFileFromDisk = new SyncFileFromDiskTool(solution, shellLocks, fileSystemTracker);
            var inlineVariable = new InlineVariableTool(solution, shellLocks, dataContexts, textControlManager);
            var changeSignature = new ChangeSignatureTool(solution, shellLocks);
            var generateXmlDoc = new GenerateXmlDocTool(solution, shellLocks);
            var codeMetrics = new CodeMetricsTool(solution, shellLocks);
            var structuralSearch = new StructuralSearchTool(solution, shellLocks, documentManager);
            var extractMethod = new ExtractMethodTool(solution, shellLocks);
            var moveType = new MoveTypeTool(solution, shellLocks, dataContexts);

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
                // get_file_errors DISABLED 2026-07-12 (see docs/DEVNOTES.md - the "flaky and low-value
                // tools" review): confirmed live to false-positive on tuple deconstruction syntax
                // ("Cannot resolve symbol 'Deconstruct'" on code that compiles and runs fine), and its
                // raw PSI-tree-walk approach is a strictly cruder duplicate of get_diagnostics, which is
                // daemon-based, more authoritative, and didn't false-positive on the same file. No
                // scenario found where this tool would be reached for over get_diagnostics. GetFileErrorsTool
                // itself is untouched - re-registering here is all that's needed to bring it back if a
                // real, distinct use case turns up.
                // McpServerTool.Create((Func<string, string>)getFileErrors.Execute,
                //     new McpServerToolCreateOptions
                //     {
                //         Name = "get_file_errors",
                //         Description = "Get compile errors and unresolved references in a file by walking the PSI " +
                //             "tree. Returns error elements with their location and description."
                //     }),
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
                            "the fix id, the bulb-action display text, and the quick-fix/highlighting .NET types. " +
                            "Entries that apply_quick_fix would refuse (quick-fix types needing an interactive " +
                            "editor session) are flagged 'NOT SUPPORTED HEADLESS' in the listing - check for that " +
                            "flag before calling apply_quick_fix on a candidate from this list."
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
                McpServerTool.Create((Func<string, string, bool, Dictionary<string, string>, bool, string>)fixUsings.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "fix_usings",
                        Description = "WRITE: fix missing using directives in C# file(s). Finds unresolved type " +
                            "references and adds using directives for unambiguous matches. Reports ambiguous " +
                            "matches with candidates; pass 'resolutions' (type name -> namespace, applied " +
                            "uniformly across all files in scope) to resolve them. Provide EXACTLY ONE of: " +
                            "'filePath' (single file, original scope), 'projectName' (every .cs file in that " +
                            "project), or scanWholeSolution=true (every .cs file in the solution). " +
                            "dryRun is only supported for 'projectName'/scanWholeSolution scope, not a single " +
                            "'filePath'."
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
                        Description = "WRITE: apply a ReSharper quick-fix (bulb action) at a position. Omit " +
                            "fixId/index to list the available fixes at that position (this listing is always " +
                            "safe and fast - it never applies anything). If exactly one fix is available it is " +
                            "applied automatically. A known set of quick-fix types that require an interactive " +
                            "editor session to complete (e.g. 'Create X from usage', 'Change all local/wrong-ref', " +
                            "'Find this type on nuget.org') are refused immediately with a clear explanation " +
                            "instead of being attempted - see docs/DEVNOTES.md 'apply_quick_fix PSI-lock wedge' " +
                            "for why. Every other fix type has applied normally and reliably in testing, but this " +
                            "is a blocklist of known-bad cases, not a structural guarantee - a not-yet-encountered " +
                            "quick-fix type that also completes via an interactive hotspot session could still " +
                            "hang the IDE until devenv.exe is restarted."
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
                // complete_at DISABLED 2026-07-12 (see docs/DEVNOTES.md - the "flaky and low-value tools"
                // review): this server only ever runs headless, and complete_at's own documented behavior
                // is to return an empty list whenever run outside an interactive editing session - i.e.
                // it can structurally never return a useful result in this server's actual deployment
                // context (confirmed live: every real test returned 0 completion items with exactly that
                // note). Also close to zero conceivable agent value even if it did work - ranked
                // completion-suggestion lists are an interactive-typing UX affordance, not something an
                // agent that writes complete expressions/statements directly benefits from.
                // CompleteAtTool itself is untouched - re-registering here is all that's needed if that
                // ever changes.
                // McpServerTool.Create((Func<string, int, int, int, string>)completeAt.Execute,
                //     new McpServerToolCreateOptions
                //     {
                //         Name = "complete_at",
                //         Description = "RISKY/best-effort, read-only: get code completion suggestions at a " +
                //             "position (LSP textDocument/completion parity). May return an empty list when run " +
                //             "outside an interactive editing session."
                //     }),
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
                            "get_diagnostics) rather than trusting a clean '(applied)' result."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, bool, bool, string>)generateXmlDoc.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "generate_xml_doc",
                        Description = "WRITE: generate an XML doc " +
                            "comment stub (<summary>/<param>/<typeparam>/<returns>/<exception>, following " +
                            "ReSharper's own template for the declaration's actual signature) for an undocumented " +
                            "symbol. Provide either a symbolName or a file path with position for a single symbol, " +
                            "OR set scanWholeFile=true with just 'filePath' to stub every undocumented PUBLIC " +
                            "member in that file. Already-documented symbols are skipped unless overwrite=true."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, bool, string>)codeMetrics.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "code_metrics",
                        Description = "READ-ONLY: compute cyclomatic " +
                            "(McCabe) complexity for a method/function-like member, or every such member in a " +
                            "whole file (scanWholeFile=true with just 'filePath', sorted worst-first). Provide " +
                            "either a symbolName or a file path with position for a single member. Complexity " +
                            "starts at 1, +1 per if/else-if, loop, catch clause, &&, ||, ?:, ??, and each " +
                            "switch case/arm - a plain PSI-tree walk, not a live daemon inspection. A nested " +
                            "local function's decision points count toward BOTH its own reported complexity AND " +
                            "its containing method's - not carved out as a separate unit - so a whole-file scan's " +
                            "per-member complexities are not a clean, non-overlapping partition of the file."
                    }),
                // structural_search RE-ENABLED 2026-07-12 (see docs/DEVNOTES.md "structural_search hang -
                // root cause and fix"): the search-mode hang (pattern "$x$.GetHashCode()", 120s+, twice)
                // was root-caused via decompilation - the SDK's own StructuralSearchRequest always drives
                // domain traversal through an async multi-threaded task-barrier fan-out
                // (SearchDomainVisitorParallel) designed for an interactive, message-pumped host thread,
                // which never completes when called from this plugin's headless dispatch thread. Fixed by
                // having StructuralSearchTool reimplement the same search using the SDK's plain synchronous
                // SearchDomainVisitor instead (see StructuralSearchTool.SearchReplaceTargetsSequential doc
                // comment for the full trail) - no locking/threading behavior was changed, just which SDK
                // traversal entry point is called. Two further SSR pattern-shape gaps remain open and
                // untouched by this fix ("$x$ == null" fails to parse; "throw new $type$($msg$)" silently
                // returns "not completed") - unrelated to the hang, just unsupported pattern shapes.
                McpServerTool.Create((Func<string, string, string, int, bool, string>)structuralSearch.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "structural_search",
                        Description = "Search for, or replace, code matching a ReSharper Structural Search " +
                            "pattern (AST-structural, not text/regex - e.g. \"$expr$.ToString()\" matches any " +
                            "ToString() call regardless of formatting/receiver expression complexity; $name$ " +
                            "placeholders match any sub-element and are auto-guessed from context, matching whole " +
                            "sub-expressions not just identifiers). Omit 'filePath' to search/replace across the " +
                            "whole solution, or scope to one file. SEARCH MODE (omit 'replacement'): READ-ONLY, " +
                            "confirmed live for literal patterns and simple placeholder patterns, including the " +
                            "pattern shape that previously hung (see docs/DEVNOTES.md) - more elaborate patterns " +
                            "(statement/type/attribute-kind, multiple placeholders, nested structure) remain " +
                            "untested, and some pattern shapes are known-unsupported (e.g. \"$x$ == null\" fails " +
                            "to parse; \"throw new $type$($msg$)\" returns 'not completed'). REPLACE MODE (provide " +
                            "'replacement', an SSR replace pattern reusing the same $name$ placeholders from " +
                            "'pattern'): WRITE, confirmed live for a placeholder-based method-call replacement " +
                            "(see docs/DEVNOTES.md) - dryRun defaults to true, pass dryRun=false to actually apply. " +
                            "CAUTION for both modes: a wrong-but-parseable pattern can silently return/replace the " +
                            "wrong thing rather than erroring for pattern shapes not yet tried - treat output with " +
                            "real skepticism until tried against known patterns with known expected results."
                    }),
                McpServerTool.Create((Func<string, int, int, int, int, string, string, bool, string>)extractMethod.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "extract_method",
                        Description = "WRITE: extract a range of " +
                            "statements into a new method (or property/local function/chained constructor, " +
                            "whichever ReSharper itself would also offer for this exact selection). Give the " +
                            "1-based statement range via startLine/startColumn/endLine/endColumn - the selection " +
                            "must cover one or more complete statements. 'newMethodName' overrides the " +
                            "auto-suggested name. 'kind' picks among the available occurrences: 'method' " +
                            "(default), 'property', 'local-function', 'chained-constructor' - if the requested " +
                            "kind isn't available for this selection, the result lists what is. Only extracting " +
                            "a STATEMENT RANGE is supported (not a single expression, not Extract Method Object). " +
                            "Set dryRun=true to preview conflicts without applying anything."
                    }),
                McpServerTool.Create((Func<string, string, string, int, int, string, bool, bool, string>)moveType.Execute,
                    new McpServerToolCreateOptions
                    {
                        Name = "move_type",
                        Description = "WRITE: move a type declaration " +
                            "(class/struct/interface/enum/delegate) into its own new file, ReSharper's 'Move to " +
                            "Another File' refactoring. Provide either a symbolName or a file path with position. " +
                            "'newFileName' overrides the default file name (the type's own name); the file " +
                            "extension is added automatically. Does NOT change the type's namespace - only " +
                            "relocates the declaration, keeping its existing namespace. 'removeOldFileIfEmpty' " +
                            "(default false) opts into deleting/renaming the original file when the moved type " +
                            "was its only declaration; left false by default so the original file (now possibly " +
                            "just an empty/near-empty shell) is left for review rather than auto-deleted. Set " +
                            "dryRun=true to preview conflicts without applying anything. KNOWN LIMITATION: a " +
                            "standalone leading comment directly above the type is NOT moved with it and is left " +
                            "orphaned at the original location - check for and manually clean up a leftover " +
                            "comment after a real move."
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
                            "pre-edit content. Also handles a brand-new file that doesn't exist in the project yet " +
                            "(e.g. just created by an external tool) - forces the same project-model resync the " +
                            "platform normally only does on VS window focus-regain, waiting up to 3s for the file " +
                            "to become visible before falling back to 'File not found in solution' if it still " +
                            "isn't (e.g. the file's directory isn't actually covered by the project's includes). " +
                            "Accepts a single 'filePath' or a 'filePaths' array to sync several files (e.g. every " +
                            "file touched by a multi-file edit) in one call."
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
