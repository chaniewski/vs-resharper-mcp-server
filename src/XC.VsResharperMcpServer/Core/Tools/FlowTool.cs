using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.DeclaredElements;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported near-verbatim from resharper-mcp's FlowTool (see docs/DEVNOTES.md) — largest single
    // tool in the reference (control-flow narration: branches, loops, error paths, call inlining,
    // comment-derived why-hints). Batch mode dropped for M3; everything else preserved as-is since
    // it already returns plain strings and needed minimal reshaping.
    public class FlowTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public FlowTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            int depth = 2,
            bool includeErrorPaths = true,
            string filePath = null,
            int line = 0,
            int column = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.flow", () =>
                ExecuteCore(symbolName, kind, depth, includeErrorPaths, filePath, line, column));
        }

        private string ExecuteCore(string symbolName, string kind, int depth, bool includeErrorPaths,
            string filePath, int line, int column)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (depth < 1) depth = 1;
            if (depth > 5) depth = 5;

            var sb = new StringBuilder();

            if (declaredElement is ITypeElement typeElement)
            {
                DescribeType(typeElement, depth, includeErrorPaths, sb);
                return sb.ToString().TrimEnd();
            }

            if (declaredElement is IProperty property)
            {
                DescribeProperty(property, depth, includeErrorPaths, sb, "");
                return sb.ToString().TrimEnd();
            }

            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count == 0)
                return $"No source declarations for '{declaredElement.ShortName}' (may be from a compiled assembly)";

            DescribeFunction(declaredElement, declarations[0], depth, includeErrorPaths, sb, "");
            return sb.ToString().TrimEnd();
        }

        // ── Type-level flow ───────────────────────────────────────────

        private void DescribeType(ITypeElement typeElement, int depth, bool includeErrorPaths, StringBuilder sb)
        {
            sb.Append("type ").AppendLine(typeElement.ShortName);

            var members = typeElement.GetMembers().ToList();

            var methods = members
                .OfType<IMethod>()
                .Where(m => !IsCompilerGenerated(m) && HasBody(m))
                .ToList();

            var properties = members
                .OfType<IProperty>()
                .Where(HasNonTrivialPropertyBody)
                .ToList();

            var ctors = methods.Where(m => m.ShortName == ".ctor").ToList();
            var regular = methods.Where(m => m.ShortName != ".ctor").OrderBy(m => m.ShortName).ToList();

            foreach (var ctor in ctors)
            {
                sb.AppendLine();
                var decl = ctor.GetDeclarations().FirstOrDefault();
                if (decl != null) DescribeFunction(ctor, decl, depth, includeErrorPaths, sb, "");
            }

            foreach (var method in regular)
            {
                sb.AppendLine();
                var decl = method.GetDeclarations().FirstOrDefault();
                if (decl != null) DescribeFunction(method, decl, depth, includeErrorPaths, sb, "");
            }

            foreach (var prop in properties)
            {
                sb.AppendLine();
                DescribeProperty(prop, depth, includeErrorPaths, sb, "");
            }

            if (ctors.Count == 0 && regular.Count == 0 && properties.Count == 0)
                sb.AppendLine("  (no non-trivial method bodies in source)");
        }

        // ── Function-level flow (method, constructor, accessor, etc.) ─

        private void DescribeFunction(IDeclaredElement element, IDeclaration decl,
            int depth, bool includeErrorPaths, StringBuilder sb, string indent)
        {
            sb.Append(indent).AppendLine(PsiHelpers.FormatSignature(element) + ":");

            var body = FindFirst<IBlock>(decl);
            if (body != null)
            {
                WalkBlock(body, depth, includeErrorPaths, sb, indent + "  ", 0);
                return;
            }

            var declText = decl.GetText();
            if (declText != null)
            {
                var arrowIdx = declText.IndexOf("=>");
                if (arrowIdx >= 0)
                {
                    var exprText = declText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                    sb.Append(indent).Append("  -> ").AppendLine(Compact(exprText));

                    var invocation = FindFirst<IInvocationExpression>(decl);
                    if (invocation != null && depth > 1)
                        TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "    ");
                    return;
                }
            }

            sb.Append(indent).AppendLine("  (no body - abstract or extern)");
        }

        // ── Property flow ─────────────────────────────────────────────

        private void DescribeProperty(IProperty property, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            sb.Append(indent).AppendLine(PsiHelpers.FormatSignature(property) + ":");

            var described = false;
            foreach (var decl in property.GetDeclarations())
            {
                foreach (var child in decl.Children())
                {
                    if (!(child is IAccessorDeclaration accessor)) continue;
                    var accText = accessor.GetText()?.TrimStart() ?? "";
                    var accKind = accText.StartsWith("set") ? "set"
                             : accText.StartsWith("init") ? "init"
                             : "get";

                    var accBody = FindFirst<IBlock>(accessor);
                    if (accBody != null)
                    {
                        sb.Append(indent).Append("  ").Append(accKind).AppendLine(":");
                        WalkBlock(accBody, depth, includeErrorPaths, sb, indent + "    ", 0);
                        described = true;
                    }
                    else
                    {
                        var accDeclText = accessor.GetText();
                        var arrowIdx = accDeclText?.IndexOf("=>") ?? -1;
                        if (arrowIdx >= 0)
                        {
                            var exprText = accDeclText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                            sb.Append(indent).Append("  ").Append(accKind).Append(" -> ")
                              .AppendLine(Compact(exprText));
                            described = true;
                        }
                    }
                }

                if (!described)
                {
                    var propText = decl.GetText();
                    var arrowIdx = propText?.IndexOf("=>") ?? -1;
                    if (arrowIdx >= 0)
                    {
                        var exprText = propText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                        sb.Append(indent).Append("  -> ").AppendLine(Compact(exprText));
                        described = true;
                    }
                }
            }

            if (!described)
                sb.Append(indent).AppendLine("  (auto-property or no source body)");
        }

        // ── Block/statement walking ───────────────────────────────────

        private void WalkBlock(IBlock block, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel)
        {
            var statements = block.Statements.ToList();
            var step = 0;

            foreach (var stmt in statements)
                WalkStatement(stmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step);

            if (step == 0)
                sb.Append(indent).AppendLine("(empty)");
        }

        private void WalkStatementBody(ICSharpStatement body, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel)
        {
            if (body is IBlock block)
                WalkBlock(block, depth, includeErrorPaths, sb, indent, nestLevel);
            else if (body != null)
            {
                var step = 0;
                WalkStatement(body, depth, includeErrorPaths, sb, indent, nestLevel, ref step);
            }
        }

        private void WalkStatement(ICSharpStatement stmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step)
        {
            var comment = GetPrecedingComment(stmt);

            if (stmt is IIfStatement guardIf && IsGuardClause(guardIf))
            {
                if (!includeErrorPaths && IsErrorGuard(guardIf)) return;

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" guard: if (")
                  .Append(Compact(guardIf.Condition?.GetText())).Append(") -> ");

                var thenBody = UnwrapBlock(guardIf.Then);
                if (thenBody is IReturnStatement ret)
                {
                    sb.Append("return");
                    if (ret.Value != null) sb.Append(' ').Append(Compact(ret.Value.GetText()));
                }
                else if (thenBody is IThrowStatement throwStmt)
                    sb.Append("throw ").Append(Compact(throwStmt.Exception?.GetText()));
                else
                    sb.Append(Compact(thenBody?.GetText()));

                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            if (stmt is IIfStatement ifStmt)
            {
                step++;
                DescribeIfChain(ifStmt, depth, includeErrorPaths, sb, indent,
                    StepLabel(nestLevel, step), nestLevel, comment);
                return;
            }

            if (stmt is ISwitchStatement switchStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" branch on ")
                  .Append(Compact(ExtractSwitchCondition(switchStmt))).Append(':');
                AppendComment(sb, comment);
                sb.AppendLine();

                foreach (var section in switchStmt.Sections)
                {
                    var labels = new List<string>();
                    foreach (var child in section.Children())
                    {
                        if (child is ICSharpStatement) break;
                        var txt = child.GetText()?.Trim();
                        if (txt != null && (txt.StartsWith("case ") || txt.StartsWith("default")))
                            labels.Add(txt.TrimEnd(':'));
                    }
                    var labelText = labels.Count > 0 ? string.Join(", ", labels) : Compact(section.GetText(), 40);
                    sb.Append(indent).Append("   ").Append(labelText).AppendLine(" ->");

                    var subStep = 0;
                    foreach (var s in section.Statements)
                    {
                        if (s is IBreakStatement) continue;
                        WalkStatement(s, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 1, ref subStep);
                    }
                }
                return;
            }

            if (stmt is ITryStatement tryStmt)
            {
                if (!includeErrorPaths)
                {
                    var tryBody = FindFirst<IBlock>(tryStmt);
                    if (tryBody != null)
                        WalkBlock(tryBody, depth, includeErrorPaths, sb, indent, nestLevel);
                    return;
                }

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" try:");
                AppendComment(sb, comment);
                sb.AppendLine();

                var firstBlock = FindFirst<IBlock>(tryStmt);
                if (firstBlock != null)
                    WalkBlock(firstBlock, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);

                foreach (var child in tryStmt.Children())
                {
                    if (child is ISpecificCatchClause specific)
                    {
                        var exType = specific.ExceptionType?.GetPresentableName(CSharpLanguage.Instance) ?? "Exception";
                        sb.Append(indent).Append("   catch ").Append(Compact(exType)).AppendLine(" ->");
                        var catchBody = FindFirst<IBlock>(specific);
                        if (catchBody != null)
                            WalkBlock(catchBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                    }
                    else if (child is IGeneralCatchClause general)
                    {
                        sb.Append(indent).AppendLine("   catch (any) ->");
                        var catchBody = FindFirst<IBlock>(general);
                        if (catchBody != null)
                            WalkBlock(catchBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                    }
                }

                foreach (var child in tryStmt.Children())
                {
                    var childText = child.GetText()?.TrimStart();
                    if (childText != null && childText.StartsWith("finally"))
                    {
                        sb.Append(indent).AppendLine("   finally ->");
                        var finallyBody = FindFirst<IBlock>(child);
                        if (finallyBody != null)
                            WalkBlock(finallyBody, depth, includeErrorPaths, sb, indent + "     ", nestLevel + 2);
                        break;
                    }
                }
                return;
            }

            if (stmt is IForeachStatement foreachStmt)
            {
                step++;
                var iterVar = ExtractForeachVariable(foreachStmt) ?? "item";
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: foreach ")
                  .Append(iterVar).Append(" in ").Append(Compact(foreachStmt.Collection?.GetText()));
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(foreachStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is IForStatement forStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: for (")
                  .Append(Compact(forStmt.Condition?.GetText() ?? "...")).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(forStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is IWhileStatement whileStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: while (")
                  .Append(Compact(whileStmt.Condition?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(whileStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is IDoStatement doStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" loop: do ... while (")
                  .Append(Compact(doStmt.Condition?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(doStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is IUsingStatement usingStmt)
            {
                step++;
                var usingHeader = ExtractHeader(usingStmt);
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" using ").Append(usingHeader);
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(usingStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is ILockStatement lockStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" lock (")
                  .Append(Compact(lockStmt.Monitor?.GetText())).Append(')');
                AppendComment(sb, comment);
                sb.AppendLine();
                WalkStatementBody(lockStmt.Body, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                return;
            }

            if (stmt is IReturnStatement returnStmt)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" return");
                if (returnStmt.Value != null)
                    sb.Append(' ').Append(Compact(returnStmt.Value.GetText()));
                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            if (stmt is IThrowStatement throwStatement)
            {
                if (!includeErrorPaths) return;
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(" throw ");
                sb.Append(throwStatement.Exception != null
                    ? Compact(throwStatement.Exception.GetText())
                    : "(rethrow)");
                AppendComment(sb, comment);
                sb.AppendLine();
                return;
            }

            if (stmt is IExpressionStatement exprStmt)
            {
                DescribeExpression(exprStmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step, comment);
                return;
            }

            if (stmt is IDeclarationStatement)
            {
                DescribeDeclaration(stmt, depth, includeErrorPaths, sb, indent, nestLevel, ref step, comment);
                return;
            }

            if (stmt is IBreakStatement || stmt is IContinueStatement || stmt is IEmptyStatement)
                return;

            step++;
            sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ')
              .Append(Compact(stmt.GetText()));
            AppendComment(sb, comment);
            sb.AppendLine();
        }

        // ── Expression statement handling ─────────────────────────────

        private void DescribeExpression(IExpressionStatement exprStmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step, string comment)
        {
            var expr = exprStmt.Expression;
            var isAwait = expr is IAwaitExpression;
            if (isAwait)
                expr = ((IAwaitExpression)expr).Task;

            if (expr is IInvocationExpression invocation)
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ');
                if (isAwait) sb.Append("await ");
                sb.Append(FormatInvocation(invocation));
                AppendComment(sb, comment);
                sb.AppendLine();

                if (depth > 1)
                    TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                return;
            }

            if (expr is IAssignmentExpression assignment)
            {
                var dest = Compact(assignment.Dest?.GetText());
                var source = assignment.Source;
                var sourceIsAwait = source is IAwaitExpression;
                if (sourceIsAwait)
                    source = ((IAwaitExpression)source).Task;

                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(dest).Append(" = ");
                if (sourceIsAwait) sb.Append("await ");

                if (source is IInvocationExpression srcInvocation)
                {
                    sb.Append(FormatInvocation(srcInvocation));
                    AppendComment(sb, comment);
                    sb.AppendLine();
                    if (depth > 1)
                        TryInlineInvocation(srcInvocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                }
                else
                {
                    sb.Append(Compact(source?.GetText()));
                    AppendComment(sb, comment);
                    sb.AppendLine();
                }
                return;
            }

            step++;
            sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ');
            if (isAwait) sb.Append("await ");
            sb.Append(Compact(expr.GetText()));
            AppendComment(sb, comment);
            sb.AppendLine();
        }

        // ── Declaration statement handling ─────────────────────────────

        private void DescribeDeclaration(ICSharpStatement stmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, int nestLevel, ref int step, string comment)
        {
            var invocation = FindFirst<IInvocationExpression>(stmt);
            if (invocation != null)
            {
                var text = stmt.GetText()?.Trim() ?? "";
                var eqIdx = text.IndexOf('=');
                var varName = "var";
                if (eqIdx > 0)
                {
                    var lhs = text.Substring(0, eqIdx).Trim();
                    var parts = lhs.Split(' ', '\t');
                    varName = parts[parts.Length - 1];
                }

                var awaitExpr = FindFirst<IAwaitExpression>(stmt);
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(varName).Append(" = ");
                if (awaitExpr != null) sb.Append("await ");
                sb.Append(FormatInvocation(invocation));
                AppendComment(sb, comment);
                sb.AppendLine();

                if (depth > 1)
                    TryInlineInvocation(invocation, depth - 1, includeErrorPaths, sb, indent + "   ");
                return;
            }

            var stmtText = stmt.GetText()?.Trim() ?? "";
            if (stmtText.Contains("="))
            {
                step++;
                sb.Append(indent).Append(StepLabel(nestLevel, step)).Append(' ').Append(Compact(stmtText));
                AppendComment(sb, comment);
                sb.AppendLine();
            }
        }

        // ── If/else chain ─────────────────────────────────────────────

        private void DescribeIfChain(IIfStatement ifStmt, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent, string label, int nestLevel, string comment)
        {
            sb.Append(indent).Append(label).Append(" if (")
              .Append(Compact(ifStmt.Condition?.GetText())).Append(')');
            AppendComment(sb, comment);
            sb.AppendLine(" ->");

            WalkStatementBody(ifStmt.Then, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);

            var elseBody = ifStmt.Else;
            while (elseBody != null)
            {
                if (elseBody is IIfStatement elseIf)
                {
                    sb.Append(indent).Append("   else if (")
                      .Append(Compact(elseIf.Condition?.GetText())).AppendLine(") ->");
                    WalkStatementBody(elseIf.Then, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                    elseBody = elseIf.Else;
                }
                else
                {
                    sb.Append(indent).AppendLine("   else ->");
                    WalkStatementBody(elseBody, depth, includeErrorPaths, sb, indent + "   ", nestLevel + 1);
                    break;
                }
            }
        }

        // ── Call inlining ─────────────────────────────────────────────

        private void TryInlineInvocation(IInvocationExpression invocation, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            if (depth <= 0) return;

            var target = ResolveInvocationTarget(invocation);
            if (target == null) return;

            InlineBody(target, depth, includeErrorPaths, sb, indent);
        }

        private void InlineBody(IDeclaredElement element, int depth, bool includeErrorPaths,
            StringBuilder sb, string indent)
        {
            if (element is ILocalFunction)
            {
                var decls = element.GetDeclarations();
                if (decls.Count == 0) return;
                if (decls[0] is ILocalFunctionDeclaration lfd)
                {
                    if (lfd.Body != null)
                    {
                        if (depth >= 2)
                            WalkBlock(lfd.Body, depth, includeErrorPaths, sb, indent, 1);
                        else
                            foreach (var stmt in lfd.Body.Statements)
                            {
                                var summary = SummarizeStatement(stmt, depth, includeErrorPaths);
                                if (summary != null)
                                    sb.Append(indent).Append("|- ").AppendLine(summary);
                            }
                        return;
                    }
                    var lfdText = lfd.GetText();
                    if (lfdText != null)
                    {
                        var arrowIdx = lfdText.IndexOf("=>");
                        if (arrowIdx >= 0)
                        {
                            var exprText = lfdText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                            sb.Append(indent).Append("|- -> ").AppendLine(Compact(exprText));
                        }
                    }
                    return;
                }
            }

            var declarations = element.GetDeclarations();
            if (declarations.Count == 0) return;

            var decl = declarations[0];

            var body = FindFirst<IBlock>(decl);
            if (body != null)
            {
                if (depth >= 2)
                {
                    WalkBlock(body, depth, includeErrorPaths, sb, indent, 1);
                }
                else
                {
                    foreach (var stmt in body.Statements)
                    {
                        var summary = SummarizeStatement(stmt, depth, includeErrorPaths);
                        if (summary != null)
                            sb.Append(indent).Append("|- ").AppendLine(summary);
                    }
                }
                return;
            }

            var declText = decl.GetText();
            if (declText != null)
            {
                var arrowIdx = declText.IndexOf("=>");
                if (arrowIdx >= 0)
                {
                    var exprText = declText.Substring(arrowIdx + 2).TrimEnd(';', ' ', '\n', '\r', '\t').Trim();
                    sb.Append(indent).Append("|- -> ").AppendLine(Compact(exprText));
                }
            }
        }

        // ── Statement summarization (for inlined calls) ───────────────

        private string SummarizeStatement(ICSharpStatement stmt, int depth, bool includeErrorPaths)
        {
            if (stmt is IIfStatement ifStmt)
            {
                if (IsGuardClause(ifStmt))
                    return "guard: if (" + Compact(ifStmt.Condition?.GetText()) + ") -> early exit";
                return "if (" + Compact(ifStmt.Condition?.GetText()) + ") -> ...";
            }

            if (stmt is ISwitchStatement switchStmt)
                return "switch on " + Compact(ExtractSwitchCondition(switchStmt));

            if (stmt is ITryStatement)
                return includeErrorPaths ? "try/catch block" : null;

            if (stmt is IForeachStatement foreachStmt)
            {
                var iterVar = ExtractForeachVariable(foreachStmt) ?? "item";
                return "foreach " + iterVar + " in " + Compact(foreachStmt.Collection?.GetText());
            }

            if (stmt is IForStatement forStmt)
                return "loop: for (" + Compact(forStmt.Condition?.GetText() ?? "...") + ")";

            if (stmt is IWhileStatement whileStmt)
                return "loop: while (" + Compact(whileStmt.Condition?.GetText()) + ")";

            if (stmt is IDoStatement doStmt)
                return "loop: do ... while (" + Compact(doStmt.Condition?.GetText()) + ")";

            if (stmt is IReturnStatement returnStmt)
                return returnStmt.Value != null ? "return " + Compact(returnStmt.Value.GetText()) : "return";

            if (stmt is IThrowStatement throwStmt)
                return includeErrorPaths ? "throw " + Compact(throwStmt.Exception?.GetText()) : null;

            if (stmt is IExpressionStatement exprStmt)
            {
                var expr = exprStmt.Expression;
                var prefix = "";
                if (expr is IAwaitExpression awaitExpr)
                {
                    expr = awaitExpr.Task;
                    prefix = "await ";
                }

                if (expr is IInvocationExpression inv)
                    return prefix + FormatInvocation(inv);

                if (expr is IAssignmentExpression assign)
                    return Compact(assign.Dest?.GetText()) + " = " + Compact(assign.Source?.GetText());

                return prefix + Compact(expr.GetText());
            }

            if (stmt is IDeclarationStatement)
            {
                var inv = FindFirst<IInvocationExpression>(stmt);
                if (inv != null)
                {
                    var text = stmt.GetText()?.Trim() ?? "";
                    var eqIdx = text.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var parts = text.Substring(0, eqIdx).Trim().Split(' ', '\t');
                        return parts[parts.Length - 1] + " = " + FormatInvocation(inv);
                    }
                    return FormatInvocation(inv);
                }
                var declText = stmt.GetText()?.Trim();
                return declText != null && declText.Contains("=") ? Compact(declText) : null;
            }

            if (stmt is IBreakStatement || stmt is IContinueStatement || stmt is IEmptyStatement)
                return null;

            return Compact(stmt.GetText());
        }

        // ── Invocation resolution ─────────────────────────────────────

        private IDeclaredElement ResolveInvocationTarget(IInvocationExpression invocation)
        {
            IDeclaredElement resolved = null;

            var invokedExpr = invocation.InvokedExpression;
            if (invokedExpr != null)
                resolved = ResolveCallableFromNode(invokedExpr);

            if (resolved == null)
                resolved = ResolveCallableFromNode(invocation);

            if (resolved == null && invokedExpr != null)
            {
                ITreeNode current = invokedExpr;
                for (int depth = 0; current != null && depth < 5; depth++)
                {
                    foreach (var reference in current.GetReferences())
                    {
                        var result = reference.Resolve();
                        if (result.DeclaredElement is IMethod m)
                        {
                            resolved = m;
                            break;
                        }
                        if (result.DeclaredElement is ILocalFunction lf)
                        {
                            resolved = lf;
                            break;
                        }
                    }
                    if (resolved != null) break;
                    current = current.Parent;
                    if (current == invocation.Parent) break;
                }
            }

            if (resolved == null) return null;

            if (resolved is IMethod method &&
                (method.IsAbstract || method.IsVirtual ||
                 method.GetContainingType() is IInterface))
            {
                var concrete = FindSingleImplementation(method);
                return concrete ?? method;
            }

            return resolved;
        }

        private static IDeclaredElement ResolveCallableFromNode(ITreeNode node)
        {
            foreach (var reference in node.GetReferences())
            {
                var result = reference.Resolve();
                if (result.DeclaredElement is IMethod method)
                    return method;
                if (result.DeclaredElement is ILocalFunction localFunc)
                    return localFunc;
            }
            return null;
        }

        private IMethod FindSingleImplementation(IMethod method)
        {
            var psiServices = _solution.GetPsiServices();
            var implementations = new List<IMethod>();

            psiServices.Finder.FindImplementingMembers(
                method,
                method.GetSearchDomain(),
                new FindResultConsumer(findResult =>
                {
                    if (implementations.Count >= 2) return FindExecution.Stop;
                    if (findResult is FindResultOverridableMember overrideResult &&
                        overrideResult.OverridableMember is IMethod impl)
                        implementations.Add(impl);
                    return FindExecution.Continue;
                }),
                true,
                NullProgressIndicator.Create());

            return implementations.Count == 1 ? implementations[0] : null;
        }

        // ── Helper methods ────────────────────────────────────────────

        private static T FindFirst<T>(ITreeNode node) where T : class, ITreeNode
        {
            for (var child = node.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is T match) return match;
                var found = FindFirst<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsGuardClause(IIfStatement ifStmt)
        {
            if (ifStmt.Else != null) return false;
            var body = UnwrapBlock(ifStmt.Then);
            return body is IReturnStatement || body is IThrowStatement;
        }

        private static bool IsErrorGuard(IIfStatement ifStmt)
        {
            return UnwrapBlock(ifStmt.Then) is IThrowStatement;
        }

        private static ICSharpStatement UnwrapBlock(ICSharpStatement stmt)
        {
            if (stmt is IBlock block && block.Statements.Count == 1)
                return block.Statements[0];
            return stmt;
        }

        private static bool HasBody(IMethod method)
        {
            var decls = method.GetDeclarations();
            if (decls.Count == 0) return false;
            var decl = decls[0];
            if (FindFirst<IBlock>(decl) != null) return true;
            var text = decl.GetText();
            return text != null && text.Contains("=>");
        }

        private static bool HasNonTrivialPropertyBody(IProperty property)
        {
            foreach (var decl in property.GetDeclarations())
            {
                for (var child = decl.FirstChild; child != null; child = child.NextSibling)
                {
                    if (child is IAccessorDeclaration accessor)
                    {
                        var body = FindFirst<IBlock>(accessor);
                        if (body != null && body.Statements.Count > 0) return true;
                        var text = accessor.GetText();
                        if (text != null && text.Contains("=>")) return true;
                    }
                }
                var propText = decl.GetText();
                if (propText != null && propText.Contains("=>")) return true;
            }
            return false;
        }

        private static bool IsCompilerGenerated(IMethod method)
        {
            var name = method.ShortName;
            if (name.StartsWith("$") || name.StartsWith("<")) return true;
            if (name.StartsWith("get_") || name.StartsWith("set_") ||
                name.StartsWith("add_") || name.StartsWith("remove_")) return true;
            if (name == "op_Equality" || name == "op_Inequality") return true;
            return false;
        }

        private static string GetPrecedingComment(ITreeNode node)
        {
            var comments = new List<string>();
            var sibling = node.PrevSibling;
            while (sibling != null)
            {
                if (sibling is ICommentNode commentNode)
                {
                    var text = commentNode.CommentText?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        comments.Insert(0, text);
                }
                else if (!(sibling is IWhitespaceNode))
                    break;
                sibling = sibling.PrevSibling;
            }
            return comments.Count > 0 ? string.Join("; ", comments) : null;
        }

        private static void AppendComment(StringBuilder sb, string comment)
        {
            if (comment != null)
                sb.Append("  // ").Append(comment);
        }

        private static string StepLabel(int nestLevel, int step)
        {
            if (nestLevel == 0) return step + ".";
            if (nestLevel == 1 && step <= 26) return ((char)('a' + step - 1)) + ".";
            return "-";
        }

        private static string FormatInvocation(IInvocationExpression invocation)
        {
            return Compact(invocation.GetText());
        }

        private static string ExtractSwitchCondition(ISwitchStatement switchStmt)
        {
            for (var child = switchStmt.FirstChild; child != null; child = child.NextSibling)
            {
                if (child is ICSharpExpression expr)
                    return expr.GetText()?.Trim();
            }
            var text = switchStmt.GetText() ?? "";
            var open = text.IndexOf('(');
            var close = text.IndexOf(')');
            if (open >= 0 && close > open)
                return text.Substring(open + 1, close - open - 1).Trim();
            return "...";
        }

        private static string ExtractForeachVariable(IForeachStatement foreachStmt)
        {
            foreach (var child in foreachStmt.Children())
            {
                if (child is IDeclaration decl)
                {
                    var name = decl.DeclaredElement?.ShortName;
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            var text = foreachStmt.GetText() ?? "";
            var inIdx = text.IndexOf(" in ");
            if (inIdx > 0)
            {
                var before = text.Substring(0, inIdx).TrimEnd();
                var lastSpace = before.LastIndexOf(' ');
                if (lastSpace >= 0)
                    return before.Substring(lastSpace + 1);
            }
            return "item";
        }

        private static string ExtractHeader(ITreeNode node)
        {
            var text = node.GetText();
            if (text == null) return "...";
            var braceIdx = text.IndexOf('{');
            if (braceIdx > 0)
                return Compact(text.Substring(0, braceIdx).Trim(), 80);
            return Compact(text, 80);
        }

        private static string Compact(string text, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(text)) return "...";
            text = Regex.Replace(text.Trim(), @"\s+", " ");
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 3) + "...";
        }
    }
}
