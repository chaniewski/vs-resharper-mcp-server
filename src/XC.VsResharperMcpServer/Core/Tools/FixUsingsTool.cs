using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Caches;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.CSharp.Impl;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Resolve;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Psi.Tree;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's FixUsingsTool (see docs/DEVNOTES.md). Batch mode and the
    // per-type-name 'resolutions' disambiguation dictionary are dropped for M4 (a plain
    // Dictionary<string,string> parameter maps cleanly through the SDK's schema inference, but the
    // batch-array mode does not — same call as the other M2/M3 tools).
    //
    // M9 (not from the reference repo - see docs/DEVNOTES.md): extended with project/solution scope.
    // NOT LIVE-TESTED - written during an autonomous unsupervised session with no VS instance available.
    // Unlike the M7 refactoring tools (extract_method/move_type), this doesn't touch any new SDK
    // surface - it reuses the single-file logic verbatim (extracted into FixUsingsInFile, unchanged
    // from the already-proven single-file path) and just loops it across a project's or solution's
    // files. The one thing worth being careful about here (per this session's apply_suggestions hang
    // postmortem): loop each file through its OWN separate PsiThreadDispatcher dispatch/transaction,
    // never one dispatch holding a lock across every file - ExecuteBulk below does exactly that.
    // 'resolutions' (if given) applies uniformly to every file in scope - e.g. "always resolve List to
    // System.Collections.Generic across this whole project" - not per-file overrides.
    public class FixUsingsTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public FixUsingsTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string filePath = null,
            string projectName = null,
            bool scanWholeSolution = false,
            Dictionary<string, string> resolutions = null,
            bool dryRun = false)
        {
            var resolutionMap = resolutions ?? new Dictionary<string, string>();
            var scopeCount = (string.IsNullOrEmpty(filePath) ? 0 : 1) +
                              (string.IsNullOrEmpty(projectName) ? 0 : 1) +
                              (scanWholeSolution ? 1 : 0);
            if (scopeCount != 1)
                return "Provide exactly one of 'filePath', 'projectName', or scanWholeSolution=true.";

            if (!string.IsNullOrEmpty(filePath))
            {
                if (dryRun)
                    return "dryRun is only supported with 'projectName' or scanWholeSolution=true, not a single 'filePath'.";

                return PsiThreadDispatcher.ExecuteWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.fix_usings", () =>
                {
                    var resolved = PsiHelpers.ResolveFile(_solution, filePath);
                    if (!resolved.IsFound)
                        return resolved.Error;

                    var csharpFile = PsiHelpers.GetPsiFile(resolved.SourceFile) as ICSharpFile;
                    if (csharpFile == null)
                        return "fix_usings only supports C# files";

                    var result = FixUsingsInFile(csharpFile, resolutionMap);
                    return FormatSingleFileResult(filePath, result);
                });
            }

            return ExecuteBulk(projectName, scanWholeSolution, resolutionMap, dryRun);
        }

        private string ExecuteBulk(string projectName, bool scanWholeSolution, Dictionary<string, string> resolutions, bool dryRun)
        {
            var projects = _solution.GetAllProjects()
                .Where(p => p.ProjectFileLocation != null && !p.ProjectFileLocation.IsEmpty);

            if (!string.IsNullOrEmpty(projectName))
            {
                projects = projects.Where(p => p.Name == projectName).ToList();
                if (!projects.Any())
                    return $"Project not found in solution: {projectName}";
            }

            var csFiles = projects
                .SelectMany(p => p.GetAllProjectFiles())
                .Where(f => f.Location.ExtensionWithDot == ".cs")
                .Select(f => f.Location.FullPath)
                .Distinct()
                .OrderBy(p => p)
                .ToList();

            if (csFiles.Count == 0)
                return "No .cs files found in scope.";

            var filesChanged = new List<(string path, FixUsingsResult result)>();
            var filesWithIssues = new List<(string path, FixUsingsResult result)>();
            var filesSkipped = new List<(string path, string reason)>();

            foreach (var path in csFiles)
            {
                var perFileResult = PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution,
                    "XC.VsResharperMcpServer.fix_usings.file", () => ExecuteOneBulkFile(path, resolutions, dryRun));

                if (perFileResult.error != null)
                {
                    filesSkipped.Add((path, perFileResult.error));
                    continue;
                }

                var result = perFileResult.result;
                if (result.Added.Count > 0)
                    filesChanged.Add((path, result));
                if (result.Ambiguous.Count > 0 || result.Unresolved.Count > 0 || result.InvalidResolutions.Count > 0)
                    filesWithIssues.Add((path, result));
            }

            return FormatBulkResult(csFiles.Count, dryRun, filesChanged, filesWithIssues, filesSkipped);
        }

        // Runs under ExecuteSelfTransactingWrite - manages its own transaction so dryRun can roll back
        // per file, matching the pattern already proven by inline_variable/change_signature/
        // apply_suggestions' ExecuteApplyLoop (never hold one lock/transaction across multiple files).
        private (FixUsingsResult result, string error) ExecuteOneBulkFile(string path, Dictionary<string, string> resolutions, bool dryRun)
        {
            var resolved = PsiHelpers.ResolveFile(_solution, path);
            if (!resolved.IsFound)
                return (null, resolved.Error);

            var csharpFile = PsiHelpers.GetPsiFile(resolved.SourceFile) as ICSharpFile;
            if (csharpFile == null)
                return (null, "not a C# file");

            var psiServices = _solution.GetPsiServices();
            using (var transaction = dryRun
                ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.fix_usings")
                : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.fix_usings"))
            {
                var result = FixUsingsInFile(csharpFile, resolutions);
                if (dryRun)
                    transaction.Rollback();
                return (result, null);
            }
        }

        // The exact same resolution logic the single-file path always used, extracted so ExecuteBulk
        // can reuse it verbatim per file rather than duplicating it.
        private static FixUsingsResult FixUsingsInFile(ICSharpFile csharpFile, Dictionary<string, string> resolutions)
        {
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

            var result = new FixUsingsResult();
            if (unresolvedByName.Count == 0)
                return result;

            var psiServices = csharpFile.GetPsiServices();
            var symbolScope = psiServices.Symbols
                .GetSymbolScope(LibrarySymbolScope.FULL, caseSensitive: true);

            var namespacesToAdd = new Dictionary<string, List<string>>();

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
                        result.Unresolved.Add(typeName);
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
                        result.InvalidResolutions.Add((typeName, chosenNs));
                    }
                }
                else
                {
                    result.Ambiguous.Add((typeName, candidateNamespaces.OrderBy(n => n).ToList()));
                }
            }

            if (namespacesToAdd.Count > 0)
            {
                var factory = CSharpElementFactory.GetInstance(csharpFile);
                foreach (var ns in namespacesToAdd.Keys.OrderBy(n => n))
                {
                    var directive = factory.CreateUsingDirective(ns);
                    UsingUtil.AddImportTo(csharpFile, directive);
                    existingNamespaces.Add(ns);
                    result.Added.Add((ns, namespacesToAdd[ns]));
                }
            }

            return result;
        }

        private static string FormatSingleFileResult(string filePath, FixUsingsResult result)
        {
            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" - fix_usings results");
            AppendResultBody(sb, result);
            return sb.ToString().TrimEnd();
        }

        private static string FormatBulkResult(int filesScanned, bool dryRun,
            List<(string path, FixUsingsResult result)> filesChanged,
            List<(string path, FixUsingsResult result)> filesWithIssues,
            List<(string path, string reason)> filesSkipped)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append("fix_usings scanned ").Append(filesScanned).AppendLine(" .cs file(s)");

            if (filesChanged.Count > 0)
            {
                sb.AppendLine();
                sb.Append(dryRun ? "would change " : "changed ").Append(filesChanged.Count).AppendLine(" file(s):");
                foreach (var (path, result) in filesChanged)
                {
                    sb.Append("  ").Append(path).AppendLine();
                    foreach (var (ns, types) in result.Added)
                        sb.Append("    + using ").Append(ns).Append("; (for: ")
                          .Append(string.Join(", ", types)).AppendLine(")");
                }
            }

            if (filesWithIssues.Count > 0)
            {
                sb.AppendLine();
                sb.Append(filesWithIssues.Count).AppendLine(" file(s) have unresolved/ambiguous references (re-run scoped to a single filePath, with 'resolutions', to fix):");
                foreach (var (path, result) in filesWithIssues)
                {
                    sb.Append("  ").Append(path).AppendLine();
                    foreach (var (typeName, namespaces) in result.Ambiguous)
                        sb.Append("    ambiguous: ").Append(typeName).Append(" - candidates: ")
                          .AppendLine(string.Join(", ", namespaces));
                    foreach (var name in result.Unresolved)
                        sb.Append("    unresolved: ").Append(name).AppendLine();
                    foreach (var (typeName, requestedNs) in result.InvalidResolutions)
                        sb.Append("    invalid resolution: ").Append(typeName).Append(" - '")
                          .Append(requestedNs).AppendLine("' is not a valid candidate");
                }
            }

            if (filesSkipped.Count > 0)
            {
                sb.AppendLine();
                sb.Append(filesSkipped.Count).AppendLine(" file(s) skipped:");
                foreach (var (path, reason) in filesSkipped)
                    sb.Append("  ").Append(path).Append(" - ").AppendLine(reason);
            }

            if (filesChanged.Count == 0 && filesWithIssues.Count == 0 && filesSkipped.Count == 0)
                sb.AppendLine("\nno fixable unresolved type references found in scope");

            return sb.ToString().TrimEnd();
        }

        private static void AppendResultBody(StringBuilder sb, FixUsingsResult result)
        {
            if (result.Added.Count > 0)
            {
                sb.AppendLine();
                sb.Append("added ").Append(result.Added.Count).AppendLine(" usings:");
                foreach (var (ns, types) in result.Added)
                    sb.Append("  using ").Append(ns).Append("; (for: ")
                      .Append(string.Join(", ", types)).AppendLine(")");
            }

            if (result.Ambiguous.Count > 0)
            {
                sb.AppendLine();
                sb.Append("ambiguous ").Append(result.Ambiguous.Count)
                  .AppendLine(" references (call again with 'resolutions' to fix):");
                foreach (var (typeName, namespaces) in result.Ambiguous)
                    sb.Append("  ").Append(typeName).Append(" - candidates: ")
                      .AppendLine(string.Join(", ", namespaces));
            }

            if (result.InvalidResolutions.Count > 0)
            {
                sb.AppendLine();
                sb.Append("invalid ").Append(result.InvalidResolutions.Count).AppendLine(" resolutions:");
                foreach (var (typeName, requestedNs) in result.InvalidResolutions)
                    sb.Append("  ").Append(typeName).Append(" - '").Append(requestedNs)
                      .AppendLine("' is not a valid candidate");
            }

            if (result.Unresolved.Count > 0)
            {
                sb.AppendLine();
                sb.Append("unresolved ").Append(result.Unresolved.Count).AppendLine(" references:");
                foreach (var name in result.Unresolved)
                    sb.Append("  ").Append(name).AppendLine(" - no matching type found");
            }

            if (result.Added.Count == 0 && result.Ambiguous.Count == 0 && result.Unresolved.Count == 0 && result.InvalidResolutions.Count == 0)
                sb.AppendLine("\nno fixable unresolved type references found");
        }

        private class FixUsingsResult
        {
            public List<(string ns, List<string> types)> Added { get; } = new List<(string, List<string>)>();
            public List<(string typeName, List<string> namespaces)> Ambiguous { get; } = new List<(string, List<string>)>();
            public List<string> Unresolved { get; } = new List<string>();
            public List<(string typeName, string requestedNs)> InvalidResolutions { get; } = new List<(string, string)>();
        }
    }
}
