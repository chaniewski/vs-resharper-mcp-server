using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetFileErrorsTool (see docs/DEVNOTES.md). Batch mode dropped for M3.
    public class GetFileErrorsTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetFileErrorsTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_file_errors", () =>
                ExecuteCore(filePath));
        }

        private string ExecuteCore(string filePath)
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

            var diagnostics = new List<DiagnosticEntry>();

            foreach (var node in psiFile.Descendants())
            {
                if (node is IErrorElement errorElement)
                {
                    var range = TreeNodeExtensions.GetDocumentRange(node);
                    if (!range.IsValid()) continue;

                    var (errLine, errCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                    diagnostics.Add(new DiagnosticEntry
                    {
                        Severity = "error",
                        Message = errorElement.ErrorDescription,
                        Line = errLine,
                        Column = errCol,
                        Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                    });
                }

                foreach (var reference in node.GetReferences())
                {
                    var resolveResult = reference.Resolve();
                    if (resolveResult.ResolveErrorType != ResolveErrorType.OK &&
                        resolveResult.ResolveErrorType != ResolveErrorType.IGNORABLE)
                    {
                        var refRange = TreeNodeExtensions.GetDocumentRange(node);
                        if (!refRange.IsValid()) continue;

                        var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                        diagnostics.Add(new DiagnosticEntry
                        {
                            Severity = resolveResult.ResolveErrorType == ResolveErrorType.DYNAMIC
                                ? "warning"
                                : "error",
                            Message = $"Cannot resolve symbol '{reference.GetName()}'",
                            Line = refLine,
                            Column = refCol,
                            Text = PsiHelpers.TruncateSnippet(node.GetText(), 200)
                        });
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - ").Append(diagnostics.Count).AppendLine(" diagnostics");

            foreach (var d in diagnostics)
            {
                sb.AppendLine();
                sb.Append(d.Severity).Append(" :").Append(d.Line).Append(':').Append(d.Column);
                sb.Append(" - ").AppendLine(d.Message);
                if (d.Text != null)
                    sb.Append("  ").AppendLine(d.Text);
            }

            return sb.ToString().TrimEnd();
        }

        private class DiagnosticEntry
        {
            public string Severity;
            public string Message;
            public int Line;
            public int Column;
            public string Text;
        }
    }
}
