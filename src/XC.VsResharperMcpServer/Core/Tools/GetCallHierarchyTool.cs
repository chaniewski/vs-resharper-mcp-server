using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetCallHierarchyTool (see docs/DEVNOTES.md), reshaped from a
    // nested Dictionary tree to an indented text tree (matches the rest of this slice's convention).
    public class GetCallHierarchyTool
    {
        private const int NodeBudget = 300;
        private const int MaxDepthCap = 4;

        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetCallHierarchyTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string direction,
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            int maxDepth = 2)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_call_hierarchy", () =>
                ExecuteCore(direction, symbolName, kind, filePath, line, column, maxDepth));
        }

        private string ExecuteCore(string directionRaw, string symbolName, string kind, string filePath, int line, int column, int maxDepth)
        {
            var direction = directionRaw?.Trim().ToLowerInvariant();
            if (direction != "incoming" && direction != "outgoing")
                return "Provide 'direction' as either 'incoming' or 'outgoing'.";

            if (maxDepth < 1) maxDepth = 1;
            if (maxDepth > MaxDepthCap) maxDepth = MaxDepthCap;

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (!(declaredElement is IFunction function))
                return $"Symbol '{declaredElement.ShortName}' is a " +
                       $"{declaredElement.GetElementType().PresentableName}, not a method/function. " +
                       "Call hierarchy is only available for methods and functions.";

            var budget = new NodeBudgetCounter();
            var visited = new HashSet<string>();
            string note = null;

            List<Node> calls;
            if (direction == "incoming")
            {
                calls = BuildIncoming(function, 0, maxDepth, visited, budget);
            }
            else
            {
                var decls = function.GetDeclarations();
                ICSharpFunctionDeclaration csDecl = null;
                foreach (var decl in decls)
                {
                    if (decl is ICSharpFunctionDeclaration f)
                    {
                        csDecl = f;
                        break;
                    }
                }

                if (csDecl == null)
                {
                    calls = new List<Node>();
                    note = "Outgoing call resolution is only supported for C# methods with an available source declaration.";
                }
                else
                {
                    calls = BuildOutgoing(csDecl, 0, maxDepth, visited, budget);
                }
            }

            var sb = new StringBuilder();
            sb.Append(function.ShortName).Append(" (").Append(PsiHelpers.GetQualifiedName(function)).Append(") - ")
              .Append(direction).Append(", maxDepth=").Append(maxDepth);
            if (budget.Truncated) sb.Append(" (truncated)");
            sb.AppendLine();
            if (note != null) sb.AppendLine("note: " + note);

            if (calls.Count == 0)
                sb.AppendLine("(no " + (direction == "incoming" ? "callers" : "callees") + " found)");

            foreach (var node in calls)
                AppendNode(sb, node, 1);

            return sb.ToString().TrimEnd();
        }

        private static void AppendNode(StringBuilder sb, Node node, int depth)
        {
            sb.Append(new string(' ', depth * 2)).Append(node.Kind).Append(' ').Append(node.Method);
            if (node.File != null)
                sb.Append(" - ").Append(node.File).Append(':').Append(node.Line);
            sb.AppendLine();
            foreach (var child in node.Children)
                AppendNode(sb, child, depth + 1);
        }

        private List<Node> BuildIncoming(
            IFunction function, int depth, int maxDepth, HashSet<string> visited, NodeBudgetCounter budget)
        {
            var children = new List<Node>();
            if (depth >= maxDepth || budget.Exhausted)
                return children;

            var fqn = PsiHelpers.GetQualifiedName(function);
            if (!visited.Add(fqn))
                return children;

            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            var seenCallers = new HashSet<string>();
            var callerEntries = new List<(IFunction callerFn, Node node)>();

            psiServices.Finder.FindReferences(
                function,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (budget.Exhausted)
                        return FindExecution.Stop;

                    if (!(findResult is FindResultReference reference))
                        return FindExecution.Continue;

                    var refNode = reference.Reference.GetTreeNode();
                    if (refNode == null)
                        return FindExecution.Continue;

                    var asExpr = refNode as ICSharpExpression;
                    var isCall = asExpr != null && InvocationExpressionNavigator.GetByInvokedExpression(asExpr) != null;

                    var callerFn = refNode.GetContainingNode<ICSharpFunctionDeclaration>()?.DeclaredElement;
                    if (callerFn == null)
                        return FindExecution.Continue;

                    var callerFqn = PsiHelpers.GetQualifiedName(callerFn);
                    if (!seenCallers.Add(callerFqn))
                        return FindExecution.Continue;

                    var node = BuildNode(callerFn, refNode, isCall, budget);
                    if (node == null)
                        return FindExecution.Continue;

                    callerEntries.Add((callerFn, node));
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            foreach (var (callerFn, node) in callerEntries)
            {
                if (budget.Exhausted) break;
                node.Children.AddRange(BuildIncoming(callerFn, depth + 1, maxDepth, visited, budget));
                children.Add(node);
            }

            visited.Remove(fqn);

            return children;
        }

        private List<Node> BuildOutgoing(
            ICSharpFunctionDeclaration declaration, int depth, int maxDepth, HashSet<string> visited, NodeBudgetCounter budget)
        {
            var children = new List<Node>();
            if (depth >= maxDepth || budget.Exhausted)
                return children;

            var declElement = declaration.DeclaredElement;
            var fqn = declElement != null ? PsiHelpers.GetQualifiedName(declElement) : null;
            if (fqn != null && !visited.Add(fqn))
                return children;

            var body = declaration.Body;
            if (body == null)
            {
                if (fqn != null) visited.Remove(fqn);
                return children;
            }

            var seenCallees = new HashSet<string>();
            var calleeEntries = new List<(IFunction calleeFn, Node node)>();

            foreach (var invocation in body.Descendants<IInvocationExpression>())
            {
                if (budget.Exhausted) break;

                var reference = invocation.InvokedExpression as IReferenceExpression;
                if (reference == null) continue;

                var callee = reference.Reference.Resolve().DeclaredElement as IFunction;
                if (callee == null) continue;

                var calleeFqn = PsiHelpers.GetQualifiedName(callee);
                if (!seenCallees.Add(calleeFqn))
                    continue;

                var node = BuildNode(callee, invocation, true, budget);
                if (node == null) continue;

                calleeEntries.Add((callee, node));
            }

            foreach (var (calleeFn, node) in calleeEntries)
            {
                if (budget.Exhausted) break;

                ICSharpFunctionDeclaration calleeDecl = null;
                foreach (var d in calleeFn.GetDeclarations())
                {
                    if (d is ICSharpFunctionDeclaration f)
                    {
                        calleeDecl = f;
                        break;
                    }
                }

                if (calleeDecl != null)
                    node.Children.AddRange(BuildOutgoing(calleeDecl, depth + 1, maxDepth, visited, budget));

                children.Add(node);
            }

            if (fqn != null) visited.Remove(fqn);

            return children;
        }

        private static Node BuildNode(IDeclaredElement element, ITreeNode siteNode, bool isCall, NodeBudgetCounter budget)
        {
            if (element == null) return null;
            if (!budget.TryConsume()) return null;

            var node = new Node
            {
                Method = PsiHelpers.GetQualifiedName(element),
                Kind = isCall ? "call" : "reference"
            };

            if (siteNode != null)
            {
                var sourceFile = siteNode.GetSourceFile();
                var range = TreeNodeExtensions.GetDocumentRange(siteNode);
                if (sourceFile != null && range.IsValid())
                {
                    var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);
                    node.File = sourceFile.GetLocation().FullPath;
                    node.Line = line;
                }
            }

            return node;
        }

        private class Node
        {
            public string Method;
            public string Kind;
            public string File;
            public int Line;
            public List<Node> Children = new List<Node>();
        }

        private class NodeBudgetCounter
        {
            private int _remaining = NodeBudget;
            public bool Truncated { get; private set; }

            public bool Exhausted => _remaining <= 0;

            public bool TryConsume()
            {
                if (_remaining <= 0)
                {
                    Truncated = true;
                    return false;
                }
                _remaining--;
                return true;
            }
        }
    }
}
