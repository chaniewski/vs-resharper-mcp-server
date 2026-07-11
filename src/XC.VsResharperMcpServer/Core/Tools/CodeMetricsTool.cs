using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M10 (not from the reference repo - see docs/DEVNOTES.md). Cyclomatic (McCabe) complexity
    // for a method/function, or every function-like member in a whole file. NOT LIVE-TESTED - written
    // during an autonomous unsupervised session with no VS instance available to test against.
    //
    // Deliberately NOT built as a daemon stage (the shape the original M10 plan item speculated about,
    // following JetBrains's own resharper-cyclomatic-complexity sample plugin) - that sample's daemon
    // integration exists because it needs to render a live gutter/inspection icon in the editor. This
    // tool only needs to return a computed number to an MCP caller, so a plain read-only PSI-tree walk
    // (matching e.g. get_call_hierarchy/get_type_hierarchy's shape, not get_diagnostics'
    // DaemonHighlightingCollector-based one) is the correct, simpler match for the actual requirement -
    // and avoids the entire DaemonHighlightingCollector/DoHighlighting hang-risk category this session's
    // apply_suggestions postmortem was about, since this tool never goes near it.
    //
    // McCabe definition used here, stated explicitly so results are interpretable: complexity starts at
    // 1 for the function itself, +1 for each: if/else-if (IIfStatement - each else-if is its own nested
    // IIfStatement, so chains are counted correctly without special-casing), for, foreach, while,
    // do-while, catch clause, && (IConditionalAndExpression), || (IConditionalOrExpression), ?:
    // (IConditionalTernaryExpression), ?? (INullCoalescingExpression), each non-default switch-statement
    // case label (ISwitchCaseLabel where !IsDefault - counts per label, not per section, so multiple
    // labels falling through to one block each still count), and each switch-EXPRESSION arm
    // (ISwitchExpressionArm - counted per arm rather than trying to special-case a discard-pattern
    // "default" arm, since ISwitchExpressionArm doesn't expose an IsDefault-equivalent the way
    // ISwitchCaseLabel does). All ten decision-point node interfaces confirmed real via decompiling
    // JetBrains.ReSharper.Psi.CSharp.dll directly (JetBrains.ReSharper.Psi.CSharp.Tree namespace) rather
    // than guessed - low SDK-shape risk, unlike extract_method/move_type's one-uncertain-step-each.
    //
    // Known, deliberate simplification: walks the WHOLE declaration node's descendants, so a local
    // function nested inside a method contributes to the OUTER method's reported complexity too (not
    // carved out as its own separately-counted unit). Local functions are also independently visited
    // and reported as their own entries in whole-file scan mode, so their complexity is visible both
    // ways - just double-counted into the parent's total, which is flagged here rather than silently
    // assumed correct.
    public class CodeMetricsTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public CodeMetricsTool(ISolution solution, IShellLocks shellLocks)
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
            bool scanWholeFile = false)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.code_metrics", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, scanWholeFile));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column, bool scanWholeFile)
        {
            if (scanWholeFile)
            {
                if (string.IsNullOrEmpty(filePath))
                    return "scanWholeFile requires 'filePath'.";
                if (!string.IsNullOrEmpty(symbolName) || line != 0 || column != 0)
                    return "scanWholeFile cannot be combined with symbolName/line/column - it targets the whole file.";

                return ExecuteWholeFile(filePath);
            }

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var elementName = declaredElement.ShortName;
            var functionDecls = declaredElement.GetDeclarations().OfType<ICSharpFunctionDeclaration>().ToList();
            if (functionDecls.Count == 0)
                return $"'{elementName}' is not a method/function-like member (constructor, operator, " +
                       "accessor, local function) - code_metrics only measures those.";

            var sb = new StringBuilder();
            sb.Append("code_metrics for '").Append(elementName).Append('\'').AppendLine();

            foreach (var decl in functionDecls)
            {
                var complexity = ComputeCyclomaticComplexity(decl);
                sb.AppendLine();
                AppendEntry(sb, decl, complexity);
            }

            return sb.ToString().TrimEnd();
        }

        private string ExecuteWholeFile(string filePath)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var csharpFile = PsiHelpers.GetPsiFile(sourceFile) as ICSharpFile;
            if (csharpFile == null)
                return "code_metrics only supports C# files";

            var entries = new List<(ICSharpFunctionDeclaration decl, int complexity)>();
            foreach (var node in csharpFile.Descendants())
            {
                if (node is ICSharpFunctionDeclaration functionDecl)
                    entries.Add((functionDecl, ComputeCyclomaticComplexity(functionDecl)));
            }

            if (entries.Count == 0)
                return $"{filePath} - no methods/function-like members found";

            entries.Sort((a, b) => b.complexity.CompareTo(a.complexity));

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - code_metrics (cyclomatic complexity), ").Append(entries.Count)
              .AppendLine(" member(s), worst first:");

            foreach (var (decl, complexity) in entries)
            {
                sb.AppendLine();
                AppendEntry(sb, decl, complexity);
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendEntry(StringBuilder sb, ICSharpFunctionDeclaration decl, int complexity)
        {
            var declaredElement = decl.DeclaredElement;
            var name = declaredElement?.ShortName ?? "(unnamed)";
            var elementKind = declaredElement?.GetElementType()?.PresentableName ?? "member";

            sb.Append(elementKind).Append(' ').Append(name)
              .Append(" - complexity: ").Append(complexity)
              .Append(" (").Append(ComplexityLabel(complexity)).Append(')');

            var range = TreeNodeExtensions.GetDocumentRange(decl);
            if (range.IsValid())
            {
                var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                sb.Append(" - :").Append(line).Append(':').Append(col);
            }

            sb.AppendLine();
        }

        // Common, informal McCabe severity bands (1-10 low/simple, 11-20 moderate, 21-50 high/complex,
        // 50+ very high/untestable) - display-only, not part of the numeric result itself.
        private static string ComplexityLabel(int complexity)
        {
            if (complexity <= 10) return "low";
            if (complexity <= 20) return "moderate";
            if (complexity <= 50) return "high";
            return "very high";
        }

        private static int ComputeCyclomaticComplexity(ITreeNode root)
        {
            var complexity = 1;
            foreach (var node in root.Descendants())
            {
                switch (node)
                {
                    case IIfStatement _:
                    case IForStatement _:
                    case IForeachStatement _:
                    case IWhileStatement _:
                    case IDoStatement _:
                    case ICatchClause _:
                    case IConditionalAndExpression _:
                    case IConditionalOrExpression _:
                    case IConditionalTernaryExpression _:
                    case INullCoalescingExpression _:
                    case ISwitchExpressionArm _:
                        complexity++;
                        break;
                    case ISwitchCaseLabel label when !label.IsDefault:
                        complexity++;
                        break;
                }
            }
            return complexity;
        }
    }
}
