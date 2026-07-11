using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GoToDefinitionTool (see docs/DEVNOTES.md). Batch mode dropped for M2.
    public class GoToDefinitionTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GoToDefinitionTool(ISolution solution, IShellLocks shellLocks)
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
            int maxTextLength = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.go_to_definition", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, maxTextLength));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column, int maxTextLength)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (maxTextLength <= 0) maxTextLength = PsiHelpers.MaxSnippetLength;
            if (maxTextLength > 50000) maxTextLength = 50000;

            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
                return $"{declaredElement.GetElementType().PresentableName} {declaredElement.ShortName}: " +
                       "no source declarations (may be from a compiled assembly)";

            var decl = declarations[0];
            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (!range.IsValid())
                return "Could not resolve declaration location";

            var declSourceFile = decl.GetSourceFile();
            if (declSourceFile == null)
                return "Declaration source file not available";

            var declFilePath = declSourceFile.GetLocation().FullPath;
            if (string.IsNullOrEmpty(declFilePath))
                declFilePath = "[no source]";

            var (declLine, declCol) = PsiHelpers.GetLineColumn(range.StartOffset);

            var sb = new StringBuilder();
            sb.Append(PsiHelpers.FormatSignature(declaredElement));
            sb.Append(" - ").Append(declFilePath);
            sb.Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
            sb.Append(PsiHelpers.TruncateSnippet(decl.GetText(), maxTextLength));

            return sb.ToString().TrimEnd();
        }
    }
}
