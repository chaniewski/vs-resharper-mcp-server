using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's ListSymbolsInFileTool (see docs/DEVNOTES.md). Batch mode dropped for M3.
    public class ListSymbolsInFileTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ListSymbolsInFileTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath, string kinds = null, bool includeLocals = false)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.list_symbols_in_file", () =>
                ExecuteCore(filePath, kinds, includeLocals));
        }

        private string ExecuteCore(string filePath, string kindsFilter, bool includeLocals)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return resolved.Error;
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return "Could not get PSI tree for file";

            var kindSet = !string.IsNullOrEmpty(kindsFilter)
                ? new HashSet<string>(kindsFilter.Split(',').Select(k => k.Trim().ToLowerInvariant()))
                : null;

            var propertyNames = new HashSet<string>();
            var eventNames = new HashSet<string>();
            foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
            {
                var el = node.DeclaredElement;
                if (el is IProperty p) propertyNames.Add(p.ShortName);
                if (el is IEvent e) eventNames.Add(e.ShortName);
            }

            var symbols = new List<SymbolEntry>();
            var seen = new HashSet<string>();

            foreach (var node in psiFile.Descendants().OfType<IDeclaration>())
            {
                var element = node.DeclaredElement;
                if (element == null) continue;
                if (element is INamespace) continue;

                if (!includeLocals && (element is ILocalVariable || element is IParameter))
                    continue;

                if (element is IMethod accessorMethod)
                {
                    var name = accessorMethod.ShortName;
                    if ((name.StartsWith("get_") || name.StartsWith("set_")) &&
                        propertyNames.Contains(name.Substring(4)))
                        continue;
                    if ((name.StartsWith("add_") || name.StartsWith("remove_")) &&
                        eventNames.Contains(name.Substring(name.IndexOf('_') + 1)))
                        continue;
                }

                if (kindSet != null && !MatchesKindFilter(element, kindSet))
                    continue;

                var range = TreeNodeExtensions.GetDocumentRange(node);
                if (!range.IsValid()) continue;

                var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);

                var key = $"{line}";
                if (!seen.Add(key)) continue;

                string containingType = null;
                if (element is IClrDeclaredElement clr)
                {
                    var ct = clr.GetContainingType();
                    if (ct != null)
                        containingType = ct.ShortName;
                }

                symbols.Add(new SymbolEntry
                {
                    Element = element,
                    ContainingType = containingType,
                    Line = line,
                    IsType = element is ITypeElement
                });
            }

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - ").Append(symbols.Count).AppendLine(" symbols");

            string lastContainingType = null;
            foreach (var sym in symbols)
            {
                if (sym.IsType)
                {
                    sb.AppendLine();
                    sb.Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                    lastContainingType = sym.Element.ShortName;
                }
                else if (sym.ContainingType != null)
                {
                    if (sym.ContainingType != lastContainingType)
                    {
                        sb.AppendLine();
                        sb.Append("(").Append(sym.ContainingType).AppendLine(")");
                        lastContainingType = sym.ContainingType;
                    }
                    sb.Append("  ").Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                }
                else
                {
                    sb.Append(PsiHelpers.FormatSignature(sym.Element));
                    sb.Append(" :").AppendLine(sym.Line.ToString());
                    lastContainingType = null;
                }
            }

            return sb.ToString().TrimEnd();
        }

        private class SymbolEntry
        {
            public IDeclaredElement Element;
            public string ContainingType;
            public int Line;
            public bool IsType;
        }

        private static bool MatchesKindFilter(IDeclaredElement element, HashSet<string> kindSet)
        {
            if (element is ITypeElement && kindSet.Contains("type")) return true;
            if (element is IMethod && kindSet.Contains("method")) return true;
            if (element is IProperty && kindSet.Contains("property")) return true;
            if (element is IField && kindSet.Contains("field")) return true;
            if (element is IEvent && kindSet.Contains("event")) return true;
            return false;
        }
    }
}
