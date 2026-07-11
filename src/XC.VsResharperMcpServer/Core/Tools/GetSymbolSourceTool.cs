using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetSymbolSourceTool (see docs/DEVNOTES.md), reshaped from a
    // structured dictionary to a formatted string (matches the rest of this slice's convention).
    public class GetSymbolSourceTool
    {
        private const int MaxSourceLength = 20000;

        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetSymbolSourceTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            bool allDeclarations = false)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_symbol_source", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, allDeclarations));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column, bool allDeclarations)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var sb = new StringBuilder();
            sb.Append(declaredElement.ShortName).Append(" (")
              .Append(PsiHelpers.GetQualifiedName(declaredElement)).AppendLine(")");

            var declarations = declaredElement.GetDeclarations();
            var wrote = false;
            if (declarations != null)
            {
                foreach (var decl in declarations)
                {
                    if (decl == null) continue;

                    var range = TreeNodeExtensions.GetDocumentRange(decl);
                    if (!range.IsValid()) continue;

                    var sourceFile = decl.GetSourceFile();
                    var (startLine, startColumn) = PsiHelpers.GetLineColumn(range.StartOffset);
                    var (endLine, endColumn) = PsiHelpers.GetLineColumn(range.EndOffset);

                    var source = range.Document.GetText(range.TextRange);
                    var truncated = false;
                    if (source != null && source.Length > MaxSourceLength)
                    {
                        source = source.Substring(0, MaxSourceLength) + "...";
                        truncated = true;
                    }

                    sb.AppendLine();
                    sb.Append(sourceFile?.GetLocation().FullPath ?? "[no source]")
                      .Append(':').Append(startLine).Append(':').Append(startColumn)
                      .Append('-').Append(endLine).Append(':').AppendLine(endColumn.ToString());
                    if (truncated) sb.AppendLine("(truncated)");
                    sb.AppendLine(source);
                    wrote = true;

                    if (!allDeclarations) break;
                }
            }

            if (!wrote)
                sb.AppendLine().AppendLine("[no source declaration available - may be from a compiled assembly]");

            return sb.ToString().TrimEnd();
        }
    }
}
