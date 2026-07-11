using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's SearchSymbolTool (see docs/DEVNOTES.md). Batch ("queries" array)
    // mode dropped for M2.
    public class SearchSymbolTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public SearchSymbolTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string query,
            string kinds = null,
            bool includeNamespaces = false,
            int maxResults = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.search_symbol", () =>
                ExecuteCore(query, kinds, includeNamespaces, maxResults));
        }

        private string ExecuteCore(string query, string kindsFilter, bool includeNamespaces, int maxResults)
        {
            if (string.IsNullOrEmpty(query))
                return "query is required";

            if (maxResults <= 0) maxResults = 50;
            if (maxResults > 200) maxResults = 200;

            var kindSet = ParseKinds(kindsFilter);
            var results = new List<SymbolResult>();
            var seen = new HashSet<string>();

            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            string qualifiedContainingType = null;
            string qualifiedMemberName = null;
            string queryLower;

            var dotIndex = query.LastIndexOf('.');
            if (dotIndex > 0 && dotIndex < query.Length - 1)
            {
                qualifiedContainingType = query.Substring(0, dotIndex).ToLowerInvariant();
                qualifiedMemberName = query.Substring(dotIndex + 1).ToLowerInvariant();
                queryLower = qualifiedMemberName;
            }
            else
            {
                queryLower = query.ToLowerInvariant();
            }

            var wantTypes = kindSet == null || kindSet.Contains("type") || kindSet.Contains("namespace");
            var wantMembers = kindSet == null || kindSet.Contains("method") || kindSet.Contains("property")
                              || kindSet.Contains("field") || kindSet.Contains("event");

            if (qualifiedContainingType != null)
            {
                SearchDotQualified(symbolScope, qualifiedContainingType, qualifiedMemberName,
                    kindSet, maxResults, results, seen);
            }
            else
            {
                if (wantTypes)
                    SearchTypes(symbolScope, queryLower, kindSet, includeNamespaces, maxResults, results, seen);

                if (wantMembers && results.Count < maxResults)
                    SearchMembers(symbolScope, queryLower, kindSet, maxResults, results, seen);
            }

            var sb = new StringBuilder();
            sb.Append("query: ").Append(query).Append(" - ").Append(results.Count).AppendLine(" results");

            foreach (var r in results)
            {
                sb.AppendLine();
                sb.Append(r.Kind).Append(' ');
                if (r.ContainingType != null)
                    sb.Append(r.ContainingType).Append('.');
                sb.Append(r.Name);
                sb.Append(" - ").Append(r.File).Append(':').Append(r.Line);
            }

            return sb.ToString().TrimEnd();
        }

        private class SymbolResult
        {
            public string Name;
            public string Kind;
            public string ContainingType;
            public string File;
            public int Line;
        }

        private void SearchTypes(ISymbolScope symbolScope, string queryLower,
            HashSet<string> kindSet, bool includeNamespaces, int maxResults,
            List<SymbolResult> results, HashSet<string> seen)
        {
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;
                if (!shortName.ToLowerInvariant().Contains(queryLower)) continue;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;

                    if (element is INamespace && !includeNamespaces &&
                        (kindSet == null || !kindSet.Contains("namespace")))
                        continue;

                    if (kindSet != null && !MatchesKindFilter(element, kindSet))
                        continue;

                    AddElementResult(element, results, seen);
                }
            }
        }

        private void SearchMembers(ISymbolScope symbolScope, string queryLower,
            HashSet<string> kindSet, int maxResults,
            List<SymbolResult> results, HashSet<string> seen)
        {
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;
                    if (!(element is ITypeElement typeElement)) continue;

                    foreach (var member in typeElement.GetMembers())
                    {
                        if (results.Count >= maxResults) break;

                        var memberName = member.ShortName;
                        if (memberName == null) continue;
                        if (!memberName.ToLowerInvariant().Contains(queryLower)) continue;
                        if (kindSet != null && !MatchesKindFilter(member, kindSet)) continue;

                        AddElementResult(member, results, seen);
                    }
                }
            }
        }

        private void SearchDotQualified(ISymbolScope symbolScope,
            string containingTypeLower, string memberNameLower,
            HashSet<string> kindSet, int maxResults,
            List<SymbolResult> results, HashSet<string> seen)
        {
            foreach (var shortName in symbolScope.GetAllShortNames())
            {
                if (results.Count >= maxResults) break;
                if (!shortName.ToLowerInvariant().Contains(containingTypeLower)) continue;

                foreach (var element in symbolScope.GetElementsByShortName(shortName))
                {
                    if (results.Count >= maxResults) break;
                    if (!(element is ITypeElement typeElement)) continue;

                    foreach (var member in typeElement.GetMembers())
                    {
                        if (results.Count >= maxResults) break;

                        var memberName = member.ShortName;
                        if (memberName == null) continue;
                        if (!memberName.ToLowerInvariant().Contains(memberNameLower)) continue;
                        if (kindSet != null && !MatchesKindFilter(member, kindSet)) continue;

                        AddElementResult(member, results, seen);
                    }
                }
            }
        }

        private static void AddElementResult(IDeclaredElement element,
            List<SymbolResult> results, HashSet<string> seen)
        {
            string filePath = null;
            int line = 0, col = 0;
            foreach (var d in element.GetDeclarations())
            {
                var sf = d.GetSourceFile();
                var path = sf?.GetLocation().FullPath;
                if (string.IsNullOrEmpty(path)) continue;

                var r = TreeNodeExtensions.GetDocumentRange(d);
                if (!r.IsValid()) continue;

                filePath = path;
                var pos = PsiHelpers.GetLineColumn(r.StartOffset);
                line = pos.line;
                col = pos.column;
                break;
            }

            if (filePath == null)
            {
                filePath = "[generated]";
                line = 0;
                col = 0;
            }

            var key = $"{filePath}:{line}:{col}";
            if (!seen.Add(key)) return;

            string containingType = null;
            if (element is IClrDeclaredElement clr)
            {
                var ct = clr.GetContainingType();
                if (ct != null)
                    containingType = ct.ShortName;
            }

            results.Add(new SymbolResult
            {
                Name = element.ShortName,
                Kind = element.GetElementType().PresentableName,
                ContainingType = containingType,
                File = filePath,
                Line = line
            });
        }

        private static HashSet<string> ParseKinds(string kindsFilter)
        {
            if (string.IsNullOrEmpty(kindsFilter)) return null;
            return new HashSet<string>(
                kindsFilter.Split(',').Select(k => k.Trim().ToLowerInvariant()));
        }

        private static bool MatchesKindFilter(IDeclaredElement element, HashSet<string> kindSet)
        {
            if (element is ITypeElement && kindSet.Contains("type")) return true;
            if (element is IMethod && kindSet.Contains("method")) return true;
            if (element is IProperty && kindSet.Contains("property")) return true;
            if (element is IField && kindSet.Contains("field")) return true;
            if (element is IEvent && kindSet.Contains("event")) return true;
            if (element is INamespace && kindSet.Contains("namespace")) return true;
            return false;
        }
    }
}
