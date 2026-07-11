using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.DocumentManagers;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CSharp.StructuralSearch;
using JetBrains.ReSharper.Feature.Services.StructuralSearch;
using JetBrains.ReSharper.Feature.Services.StructuralSearch.Finding;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // M8 spike (not from the reference repo - see docs/DEVNOTES.md). Structural Search (read-only half
    // only, per the user's explicit sequencing: spike search, confirm it's headlessly drivable, ship it,
    // decide on replace separately later). NOT LIVE-TESTED - written during an autonomous unsupervised
    // session with no VS instance available to test against.
    //
    // SPIKE RESULT: YES, genuinely headlessly drivable, and much more directly than any of this
    // session's other M7-M10 refactoring/write tools - no synthetic-IDataContext technique, no
    // protected-setter workaround, no UI-wizard entanglement of the kind that made safe_delete
    // unfinishable. Confirmed by decompiling JetBrains.ReSharper.Feature.Services.dll /
    // JetBrains.ReSharper.Feature.Services.CSharp.dll directly:
    //   - JetBrains.ReSharper.Feature.Services.CSharp.StructuralSearch.CSharpStructuralSearchPattern has
    //     a plain public constructor taking just the pattern text: new CSharpStructuralSearchPattern(text).
    //   - JetBrains.ReSharper.Feature.Services.StructuralSearch.Finding.StructuralSearchRequest is a
    //     real, directly-constructible, non-UI class - SearchReplaceTargets(IProgressIndicator) runs the
    //     whole search and returns the raw IList<IStructuralMatchResult> matches directly, no UI/tool
    //     window/results-browser involved anywhere in the call path (that machinery - the ~40-class
    //     JetBrains.ReSharper.Features.StructuralSearch.dll assembly with its results-browser
    //     descriptors/icons/recent-searches-manager - is a SEPARATE, purely presentational layer this
    //     tool never touches).
    //   - The search domain (file/solution scope) comes from JetBrains.ReSharper.Psi.Search.
    //     SearchDomainFactory, reachable via the already-injected ISolution
    //     (_solution.GetPsiServices().SearchDomainFactory - its own doc comment explicitly recommends
    //     this over DI injection) - CreateSearchDomain(ISolution, bool) and CreateSearchDomain
    //     (IPsiSourceFile) cover whole-solution and single-file scope respectively.
    //   - The engine itself (StructuralSearcher/StructuralSearchDomainSearcher, decompiled) is a
    //     dedicated find-in-files-style searcher using IWordIndex - it never goes near
    //     DaemonHighlightingCollector/DoHighlighting, so this tool is entirely outside the hang-risk
    //     category apply_suggestions' postmortem was about. Dispatches via ExecuteRead, matching that.
    //
    // CAUTION, stronger than for this batch's other five tools: compiling cleanly means LESS here than
    // it did for code_metrics/generate_xml_doc. Every type/constructor/method used above is confirmed
    // real via decompilation, so the WIRING is solid - but whether any given SSR pattern string actually
    // PARSES and MATCHES correctly against real code is completely unverified. A pattern that compiles
    // fine at the C# level (this tool's own code) can still silently match nothing, match everything, or
    // throw at pattern-parse time for a syntax this tool has never actually exercised - none of that
    // would show up as a compile error. Treat any output from this tool with real skepticism until it's
    // been run live against a few known patterns with known expected results.
    //
    // v1 scope: search only, matching the user's own explicit sequencing ("spike the search-only half,
    // confirm it's headlessly drivable, ship read-only search, then decide separately whether replace is
    // worth the write-tool risk surface"). Replace (CSharpStructuralSearchReplacer, also seen in the
    // decompiled type list) is NOT implemented here - deliberately deferred, not attempted and abandoned.
    public class StructuralSearchTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly DocumentManager _documentManager;

        public StructuralSearchTool(ISolution solution, IShellLocks shellLocks, DocumentManager documentManager)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _documentManager = documentManager;
        }

        public string Execute(string pattern, string filePath = null, int maxResults = 50)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.structural_search", () =>
                ExecuteCore(pattern, filePath, maxResults));
        }

        private string ExecuteCore(string pattern, string filePath, int maxResults)
        {
            if (string.IsNullOrEmpty(pattern))
                return "pattern is required (e.g. \"$expr$.ToString()\" - see ReSharper's own Structural " +
                       "Search and Replace syntax; $name$ placeholders match any sub-element).";

            var searchDomainFactory = _solution.GetPsiServices().SearchDomainFactory;

            ISearchDomain searchDomain;
            if (!string.IsNullOrEmpty(filePath))
            {
                var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
                if (sourceFile == null)
                    return $"File not found in solution: {filePath}";
                searchDomain = searchDomainFactory.CreateSearchDomain(sourceFile);
            }
            else
            {
                searchDomain = searchDomainFactory.CreateSearchDomain(_solution, includeLibraries: false);
            }

            var ssrPattern = new CSharpStructuralSearchPattern(pattern);
            var request = new StructuralSearchRequest(_solution, _documentManager, searchDomainFactory, searchDomain, ssrPattern);

            IList<IStructuralMatchResult> matches;
            try
            {
                matches = request.SearchReplaceTargets(NullProgressIndicator.Create());
            }
            catch (Exception ex)
            {
                return $"structural_search failed - the pattern likely couldn't be parsed: {ex.Message}";
            }

            if (matches == null)
                return "Search did not complete (cancelled, or the pattern was rejected before matching started).";

            if (matches.Count == 0)
                return $"No matches found for pattern: {pattern}";

            var sb = new StringBuilder();
            sb.Append("structural_search: ").Append(matches.Count).Append(" match(es) for pattern: ").AppendLine(pattern);

            var shown = 0;
            foreach (var match in matches)
            {
                if (shown >= maxResults)
                {
                    sb.AppendLine().Append("... (truncated, ").Append(matches.Count - maxResults).Append(" more not shown)");
                    break;
                }

                var element = match.MatchedElement;
                if (element == null) continue;

                var sourceFile = element.GetSourceFile();
                var path = sourceFile?.GetLocation().FullPath ?? "(unknown file)";

                sb.AppendLine();
                sb.Append(path);

                var range = match.GetDocumentRange();
                if (range.IsValid())
                {
                    var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                    sb.Append(" :").Append(line).Append(':').Append(col);
                }

                var text = element.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    var snippet = text.Length > 150 ? text.Substring(0, 150) + "..." : text;
                    sb.Append(" - ").Append(snippet.Replace("\r\n", " ").Replace('\n', ' '));
                }

                shown++;
            }

            return sb.ToString().TrimEnd();
        }
    }
}
