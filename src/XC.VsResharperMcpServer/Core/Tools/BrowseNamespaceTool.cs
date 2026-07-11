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
    // Ported from resharper-mcp's BrowseNamespaceTool (see docs/DEVNOTES.md). Batch mode dropped for M3.
    public class BrowseNamespaceTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public BrowseNamespaceTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string namespaceName = null)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.browse_namespace", () =>
                ExecuteCore(namespaceName ?? ""));
        }

        private string ExecuteCore(string namespaceName)
        {
            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.NONE, caseSensitive: true);

            INamespace targetNs;
            if (string.IsNullOrEmpty(namespaceName))
            {
                targetNs = symbolScope.GlobalNamespace;
            }
            else
            {
                targetNs = FindNamespace(symbolScope, namespaceName);
                if (targetNs == null)
                    return $"Namespace not found: {namespaceName}";
            }

            var sb = new StringBuilder();
            var displayName = string.IsNullOrEmpty(namespaceName) ? "(root)" : namespaceName;
            sb.Append("namespace: ").AppendLine(displayName);

            var childNamespaces = targetNs.GetNestedNamespaces(symbolScope)
                .OrderBy(ns => ns.ShortName)
                .ToList();

            if (childNamespaces.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("child namespaces:");
                foreach (var childNs in childNamespaces)
                {
                    var typeCount = childNs.GetNestedTypeElements(symbolScope).Count();
                    sb.Append("  ").Append(childNs.QualifiedName);
                    sb.Append(" (").Append(typeCount).AppendLine(" types)");
                }
            }

            var types = targetNs.GetNestedTypeElements(symbolScope)
                .Where(te => te.GetContainingType() == null)
                .OrderBy(te => te.ShortName)
                .ToList();

            if (types.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("types:");
                foreach (var typeElement in types)
                {
                    sb.Append("  ").Append(typeElement.GetElementType().PresentableName);
                    sb.Append(' ').Append(typeElement.ShortName);

                    var declarations = typeElement.GetDeclarations();
                    if (declarations.Count > 0)
                    {
                        var decl = declarations[0];
                        var sf = decl.GetSourceFile();
                        if (sf != null)
                        {
                            var filePath = sf.GetLocation().FullPath;
                            var fileName = System.IO.Path.GetFileName(filePath);

                            if (IsGeneratedFile(fileName))
                                sb.Append(" [generated]");

                            sb.Append(" - ").Append(fileName);

                            var range = TreeNodeExtensions.GetDocumentRange(decl);
                            if (range.IsValid())
                            {
                                var (line, _) = PsiHelpers.GetLineColumn(range.StartOffset);
                                sb.Append(':').Append(line);
                            }
                        }
                    }

                    sb.AppendLine();
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static bool IsGeneratedFile(string fileName)
        {
            return fileName.EndsWith(".g.cs") ||
                   fileName.EndsWith(".g.fs") ||
                   fileName.EndsWith(".generated.cs") ||
                   fileName.EndsWith(".designer.cs", System.StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".g.i.cs") ||
                   fileName == "AssemblyInfo.cs" ||
                   fileName == "AssemblyAttributes.cs" ||
                   fileName.EndsWith(".razor.g.cs");
        }

        private static INamespace FindNamespace(ISymbolScope symbolScope, string qualifiedName)
        {
            var parts = qualifiedName.Split('.');
            var current = symbolScope.GlobalNamespace;

            foreach (var part in parts)
            {
                INamespace found = null;
                foreach (var child in current.GetNestedNamespaces(symbolScope))
                {
                    if (child.ShortName == part)
                    {
                        found = child;
                        break;
                    }
                }

                if (found == null) return null;
                current = found;
            }

            return current;
        }
    }
}
