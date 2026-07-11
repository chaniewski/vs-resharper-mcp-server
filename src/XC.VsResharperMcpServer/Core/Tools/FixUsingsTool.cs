using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Impl;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's FixUsingsTool (see docs/DEVNOTES.md). Batch mode and the
    // per-type-name 'resolutions' disambiguation dictionary are dropped for M4 (a plain
    // Dictionary<string,string> parameter maps cleanly through the SDK's schema inference, but the
    // batch-array mode does not — same call as the other M2/M3 tools).
    public class FixUsingsTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public FixUsingsTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath, Dictionary<string, string> resolutions = null)
        {
            return PsiThreadDispatcher.ExecuteWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.fix_usings", () =>
                ExecuteCore(filePath, resolutions ?? new Dictionary<string, string>()));
        }

        private string ExecuteCore(string filePath, Dictionary<string, string> resolutions)
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

            var csharpFile = psiFile as ICSharpFile;
            if (csharpFile == null)
                return "fix_usings only supports C# files";

            var existingNamespaces = new HashSet<string>();
            foreach (var import in csharpFile.ImportsEnumerable)
            {
                var nsDir = import as IUsingSymbolDirective;
                if (nsDir?.ImportedSymbolName != null)
                {
                    var ns = nsDir.ImportedSymbolName.QualifiedName;
                    if (!string.IsNullOrEmpty(ns))
                        existingNamespaces.Add(ns);
                }
            }

            var unresolvedByName = new Dictionary<string, List<string>>();
            foreach (var node in csharpFile.Descendants())
            {
                if (!(node is IReferenceName))
                    continue;

                foreach (var reference in node.GetReferences())
                {
                    var resolveResult = reference.Resolve();
                    if (resolveResult.ResolveErrorType == ResolveErrorType.OK ||
                        resolveResult.ResolveErrorType == ResolveErrorType.IGNORABLE)
                        continue;

                    var name = reference.GetName();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    if (!unresolvedByName.ContainsKey(name))
                        unresolvedByName[name] = new List<string>();

                    var range = TreeNodeExtensions.GetDocumentRange(node);
                    if (range.IsValid())
                    {
                        var (line, col) = PsiHelpers.GetLineColumn(range.StartOffset);
                        var loc = $"{line}:{col}";
                        if (!unresolvedByName[name].Contains(loc))
                            unresolvedByName[name].Add(loc);
                    }
                }
            }

            if (unresolvedByName.Count == 0)
                return $"{filePath} - no unresolved type references found";

            var psiServices = _solution.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true);

            var namespacesToAdd = new Dictionary<string, List<string>>();
            var ambiguous = new List<(string typeName, List<string> namespaces)>();
            var unresolved = new List<string>();
            var invalidResolutions = new List<(string typeName, string requestedNs)>();

            foreach (var kvp in unresolvedByName)
            {
                var typeName = kvp.Key;

                var candidateNamespaces = new HashSet<string>();
                foreach (var element in symbolScope.GetElementsByShortName(typeName))
                {
                    var typeElement = element as ITypeElement;
                    if (typeElement == null) continue;

                    var ns = typeElement.GetContainingNamespace();
                    if (ns != null && !string.IsNullOrEmpty(ns.QualifiedName))
                        candidateNamespaces.Add(ns.QualifiedName);
                }

                candidateNamespaces.ExceptWith(existingNamespaces);

                if (candidateNamespaces.Count == 0)
                {
                    var anyCandidates = symbolScope.GetElementsByShortName(typeName)
                        .Any(e => e is ITypeElement);
                    if (!anyCandidates)
                        unresolved.Add(typeName);
                }
                else if (candidateNamespaces.Count == 1)
                {
                    var ns = candidateNamespaces.First();
                    if (!namespacesToAdd.ContainsKey(ns))
                        namespacesToAdd[ns] = new List<string>();
                    namespacesToAdd[ns].Add(typeName);
                }
                else if (resolutions.TryGetValue(typeName, out var chosenNs))
                {
                    if (candidateNamespaces.Contains(chosenNs))
                    {
                        if (!namespacesToAdd.ContainsKey(chosenNs))
                            namespacesToAdd[chosenNs] = new List<string>();
                        namespacesToAdd[chosenNs].Add(typeName);
                    }
                    else
                    {
                        invalidResolutions.Add((typeName, chosenNs));
                    }
                }
                else
                {
                    ambiguous.Add((typeName, candidateNamespaces.OrderBy(n => n).ToList()));
                }
            }

            var added = new List<(string ns, List<string> types)>();
            if (namespacesToAdd.Count > 0)
            {
                var factory = CSharpElementFactory.GetInstance(csharpFile);
                foreach (var ns in namespacesToAdd.Keys.OrderBy(n => n))
                {
                    var directive = factory.CreateUsingDirective(ns);
                    UsingUtil.AddImportTo(csharpFile, directive);
                    existingNamespaces.Add(ns);
                    added.Add((ns, namespacesToAdd[ns]));
                }
            }

            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" - fix_usings results");

            if (added.Count > 0)
            {
                sb.AppendLine();
                sb.Append("added ").Append(added.Count).AppendLine(" usings:");
                foreach (var (ns, types) in added)
                    sb.Append("  using ").Append(ns).Append("; (for: ")
                      .Append(string.Join(", ", types)).AppendLine(")");
            }

            if (ambiguous.Count > 0)
            {
                sb.AppendLine();
                sb.Append("ambiguous ").Append(ambiguous.Count)
                  .AppendLine(" references (call again with 'resolutions' to fix):");
                foreach (var (typeName, namespaces) in ambiguous)
                    sb.Append("  ").Append(typeName).Append(" - candidates: ")
                      .AppendLine(string.Join(", ", namespaces));
            }

            if (invalidResolutions.Count > 0)
            {
                sb.AppendLine();
                sb.Append("invalid ").Append(invalidResolutions.Count).AppendLine(" resolutions:");
                foreach (var (typeName, requestedNs) in invalidResolutions)
                    sb.Append("  ").Append(typeName).Append(" - '").Append(requestedNs)
                      .AppendLine("' is not a valid candidate");
            }

            if (unresolved.Count > 0)
            {
                sb.AppendLine();
                sb.Append("unresolved ").Append(unresolved.Count).AppendLine(" references:");
                foreach (var name in unresolved)
                    sb.Append("  ").Append(name).AppendLine(" - no matching type found");
            }

            if (added.Count == 0 && ambiguous.Count == 0 && unresolved.Count == 0 && invalidResolutions.Count == 0)
                sb.AppendLine("\nno fixable unresolved type references found");

            return sb.ToString().TrimEnd();
        }
    }
}
