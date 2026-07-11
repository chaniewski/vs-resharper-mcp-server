using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Search;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetTypeHierarchyTool (see docs/DEVNOTES.md), reshaped from a
    // nested object tree to an indented text tree (matches the rest of this slice's convention).
    public class GetTypeHierarchyTool
    {
        private const int NodeBudget = 500;

        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetTypeHierarchyTool(ISolution solution, IShellLocks shellLocks)
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
            int maxDepth = 3)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_type_hierarchy", () =>
                ExecuteCore(direction, symbolName, kind, filePath, line, column, maxDepth));
        }

        private string ExecuteCore(string directionRaw, string symbolName, string kind, string filePath, int line, int column, int maxDepth)
        {
            if (string.IsNullOrEmpty(directionRaw))
                return "Provide 'direction': either 'supertypes' or 'subtypes'.";

            var direction = directionRaw.ToLowerInvariant();
            if (direction != "supertypes" && direction != "subtypes")
                return $"Invalid direction '{direction}'. Use 'supertypes' or 'subtypes'.";

            if (maxDepth < 1) maxDepth = 1;

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (!(declaredElement is ITypeElement typeElement))
                return $"Symbol '{declaredElement.ShortName}' is not a type. Type hierarchy requires a class, interface, struct, or other type.";

            var budget = new NodeCounter(NodeBudget);
            var visited = new HashSet<string>();
            var rootFqn = SafeFullName(typeElement);
            if (rootFqn != null) visited.Add(rootFqn);

            var children = direction == "subtypes"
                ? BuildSubtypes(typeElement, 1, maxDepth, visited, budget)
                : BuildSupertypes(typeElement, 1, maxDepth, visited, budget);

            var sb = new StringBuilder();
            sb.Append(typeElement.ShortName).Append(" (").Append(PsiHelpers.GetQualifiedName(typeElement)).Append(") - ")
              .Append(direction).Append(", maxDepth=").Append(maxDepth);
            if (budget.Exhausted) sb.Append(" (truncated)");
            sb.AppendLine();

            if (children.Count == 0)
                sb.AppendLine("(none)");

            foreach (var node in children)
                AppendNode(sb, node, 1);

            return sb.ToString().TrimEnd();
        }

        private static void AppendNode(StringBuilder sb, Node node, int depth)
        {
            sb.Append(new string(' ', depth * 2)).Append('[').Append(node.Relation).Append("] ").Append(node.Type);
            if (node.File != null)
                sb.Append(" - ").Append(node.File).Append(':').Append(node.Line);
            sb.AppendLine();
            foreach (var child in node.Children)
                AppendNode(sb, child, depth + 1);
        }

        private List<Node> BuildSubtypes(
            ITypeElement typeElement, int depth, int maxDepth,
            HashSet<string> visited, NodeCounter budget)
        {
            var result = new List<Node>();
            if (depth > maxDepth || budget.Exhausted) return result;

            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            var inheritors = new List<ITypeElement>();
            psiServices.Finder.FindInheritors(
                typeElement,
                searchDomain,
                new FindResultConsumer(findResult =>
                {
                    if (findResult is FindResultInheritedElement inherited &&
                        inherited.DeclaredElement is ITypeElement inheritedType)
                        inheritors.Add(inheritedType);
                    return FindExecution.Continue;
                }),
                NullProgressIndicator.Create());

            foreach (var inheritor in inheritors)
            {
                if (budget.Exhausted) break;

                if (!IsDirectInheritor(inheritor, typeElement)) continue;

                var fqn = SafeFullName(inheritor);
                var alreadyVisited = fqn != null && !visited.Add(fqn);

                if (!budget.TryConsume()) break;

                var node = BuildNode(inheritor, RelationTo(typeElement));

                if (!alreadyVisited)
                    node.Children.AddRange(BuildSubtypes(inheritor, depth + 1, maxDepth, visited, budget));

                result.Add(node);
            }

            return result;
        }

        private List<Node> BuildSupertypes(
            ITypeElement typeElement, int depth, int maxDepth,
            HashSet<string> visited, NodeCounter budget)
        {
            var result = new List<Node>();
            if (depth > maxDepth || budget.Exhausted) return result;

            foreach (var superType in typeElement.GetSuperTypes())
            {
                if (budget.Exhausted) break;

                var resolved = superType.GetTypeElement();
                if (resolved == null) continue;

                if (IsSystemObject(resolved)) continue;

                var fqn = SafeFullName(resolved);
                var alreadyVisited = fqn != null && !visited.Add(fqn);

                if (!budget.TryConsume()) break;

                var node = BuildNode(resolved, resolved is IInterface ? "implements" : "extends");

                if (!alreadyVisited)
                    node.Children.AddRange(BuildSupertypes(resolved, depth + 1, maxDepth, visited, budget));

                result.Add(node);
            }

            return result;
        }

        private static string RelationTo(ITypeElement supertype)
        {
            return supertype is IInterface ? "implements" : "extends";
        }

        private static bool IsDirectInheritor(ITypeElement inheritor, ITypeElement baseType)
        {
            var baseFqn = SafeFullName(baseType);
            if (baseFqn == null) return false;

            foreach (var superType in inheritor.GetSuperTypes())
            {
                var resolved = superType.GetTypeElement();
                if (resolved != null && SafeFullName(resolved) == baseFqn)
                    return true;
            }
            return false;
        }

        private static bool IsSystemObject(ITypeElement element)
        {
            return SafeFullName(element) == "System.Object";
        }

        private static Node BuildNode(ITypeElement element, string relation)
        {
            var node = new Node
            {
                Type = PsiHelpers.GetQualifiedName(element),
                Relation = relation
            };

            var declarations = element.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var range = TreeNodeExtensions.GetDocumentRange(decl);
                var sourceFile = decl.GetSourceFile();
                if (range.IsValid() && sourceFile != null)
                {
                    var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);
                    node.File = sourceFile.GetLocation().FullPath;
                    node.Line = line;
                }
            }

            return node;
        }

        private static string SafeFullName(ITypeElement element)
        {
            var clrName = element.GetClrName();
            return clrName?.FullName;
        }

        private class Node
        {
            public string Type;
            public string Relation;
            public string File;
            public int Line;
            public List<Node> Children = new List<Node>();
        }

        private sealed class NodeCounter
        {
            private int _remaining;

            public NodeCounter(int budget) => _remaining = budget;

            public bool Exhausted { get; private set; }

            public bool TryConsume()
            {
                if (_remaining <= 0)
                {
                    Exhausted = true;
                    return false;
                }
                _remaining--;
                return true;
            }
        }
    }
}
