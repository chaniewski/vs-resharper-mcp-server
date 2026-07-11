using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // Ported from resharper-mcp's FindUsagesTool (see docs/DEVNOTES.md). Batch ("symbols" array)
    // mode is dropped for the M2 slice — single-symbol lookups only for now.
    public class FindUsagesTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public FindUsagesTool(ISolution solution, IShellLocks shellLocks)
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
            bool excludeDeclarationFile = false,
            int maxResults = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.find_usages", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, excludeDeclarationFile, maxResults));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column,
            bool excludeDeclFile, int maxResults)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (maxResults < 0) maxResults = 0;
            var hitLimit = false;

            var declFilePaths = new HashSet<string>();
            if (excludeDeclFile)
            {
                foreach (var decl in declaredElement.GetDeclarations())
                {
                    var sf = decl.GetSourceFile();
                    if (sf != null)
                        declFilePaths.Add(sf.GetLocation().FullPath);
                }
            }

            var rawUsages = new List<RawUsage>();
            var psiServices = _solution.GetPsiServices();
            var searchDomain = SearchDomainFactory.Instance.CreateSearchDomain(_solution, false);

            FindExecution HandleResult(FindResult findResult)
            {
                if (maxResults > 0 && rawUsages.Count >= maxResults)
                {
                    hitLimit = true;
                    return FindExecution.Stop;
                }
                if (findResult is FindResultReference reference)
                {
                    var refNode = reference.Reference.GetTreeNode();
                    var refSourceFile = refNode.GetSourceFile();
                    if (refSourceFile != null)
                    {
                        var refFilePath = refSourceFile.GetLocation().FullPath;
                        if (string.IsNullOrEmpty(refFilePath))
                            return FindExecution.Continue;

                        if (excludeDeclFile && declFilePaths.Contains(refFilePath))
                            return FindExecution.Continue;

                        var refRange = TreeNodeExtensions.GetDocumentRange(refNode);
                        if (refRange.IsValid())
                        {
                            var (refLine, refCol) = PsiHelpers.GetLineColumn(refRange.StartOffset);
                            var projectName = refSourceFile.GetProject()?.Name;
                            rawUsages.Add(new RawUsage
                            {
                                Project = projectName ?? "(unknown)",
                                File = refFilePath,
                                Line = refLine,
                                Column = refCol,
                                Text = PsiHelpers.TruncateSnippet(refNode.Parent?.GetText() ?? refNode.GetText())
                            });
                        }
                    }
                }
                return FindExecution.Continue;
            }

            psiServices.Finder.FindReferences(
                declaredElement, searchDomain, new FindResultConsumer(HandleResult), NullProgressIndicator.Create());

            var superMembers = FindInterfaceMembers(declaredElement);
            foreach (var superMember in superMembers)
            {
                if (hitLimit) break;
                psiServices.Finder.FindReferences(
                    superMember, searchDomain, new FindResultConsumer(HandleResult), NullProgressIndicator.Create());
            }

            var deduped = rawUsages
                .GroupBy(u => $"{u.File}:{u.Line}")
                .Select(g => g.First())
                .ToList();

            var sb = new StringBuilder();
            var fileCount = deduped.Select(u => u.File).Distinct().Count();
            var projectCount = deduped.Select(u => u.Project).Distinct().Count();

            sb.Append(declaredElement.GetElementType().PresentableName).Append(' ');
            sb.Append(declaredElement.ShortName);
            sb.Append(" - ").Append(deduped.Count);
            if (hitLimit) sb.Append('+');
            sb.Append(" usages in ");
            sb.Append(fileCount).Append(" files, ");
            sb.Append(projectCount).AppendLine(" projects");
            if (hitLimit)
                sb.AppendLine("(limit reached; increase maxResults to see more)");

            if (superMembers.Count > 0)
            {
                sb.Append("(includes usages via: ");
                sb.Append(string.Join(", ", superMembers.Select(PsiHelpers.GetQualifiedName)));
                sb.AppendLine(")");
            }

            if (deduped.Count == 0 && superMembers.Count == 0)
            {
                var implNote = GetImplementationNote(declaredElement);
                if (implNote != null)
                    sb.AppendLine(implNote);
            }

            var declarations = declaredElement.GetDeclarations();
            if (declarations.Count > 0)
            {
                var decl = declarations[0];
                var declRange = TreeNodeExtensions.GetDocumentRange(decl);
                if (declRange.IsValid())
                {
                    var declSourceFile = decl.GetSourceFile();
                    if (declSourceFile != null)
                    {
                        var declPath = declSourceFile.GetLocation().FullPath;
                        if (string.IsNullOrEmpty(declPath))
                            declPath = "[no source]";
                        var (declLine, declCol) = PsiHelpers.GetLineColumn(declRange.StartOffset);
                        sb.Append("declared: ").Append(declPath)
                          .Append(':').Append(declLine).Append(':').AppendLine(declCol.ToString());
                    }
                }
            }

            var grouped = deduped
                .GroupBy(u => u.Project)
                .OrderByDescending(g => g.Count());

            foreach (var projectGroup in grouped)
            {
                sb.AppendLine();
                sb.Append("--- ").Append(projectGroup.Key).Append(" (")
                  .Append(projectGroup.Count()).AppendLine(" usages) ---");

                foreach (var fileGroup in projectGroup.GroupBy(u => u.File).OrderByDescending(g => g.Count()))
                {
                    sb.AppendLine(Path.GetFileName(fileGroup.Key));
                    foreach (var u in fileGroup)
                    {
                        sb.Append("  :").Append(u.Line).Append(':').Append(u.Column)
                          .Append(" - ").AppendLine(u.Text);
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static List<IDeclaredElement> FindInterfaceMembers(IDeclaredElement element)
        {
            var result = new List<IDeclaredElement>();
            if (!(element is IClrDeclaredElement clrElem)) return result;

            var containingType = clrElem.GetContainingType();
            if (containingType == null) return result;

            var memberName = element.ShortName;
            var paramCount = (element as IParametersOwner)?.Parameters.Count ?? -1;
            var visited = new HashSet<string>();

            CollectInterfaceMembers(containingType, memberName, paramCount, element, result, visited);
            return result;
        }

        private static void CollectInterfaceMembers(ITypeElement type, string memberName, int paramCount,
            IDeclaredElement originalMember, List<IDeclaredElement> result, HashSet<string> visited)
        {
            foreach (var superType in type.GetSuperTypes())
            {
                var superElement = superType.GetTypeElement();
                if (superElement == null) continue;

                var fqn = superElement.GetClrName().FullName;
                if (!visited.Add(fqn)) continue;

                if (superElement is IInterface)
                {
                    foreach (var m in superElement.GetMembers())
                    {
                        if (m.ShortName != memberName) continue;
                        if (m.Equals(originalMember)) continue;
                        if (paramCount >= 0 && m is IParametersOwner po && po.Parameters.Count != paramCount)
                            continue;
                        result.Add(m);
                    }
                }

                CollectInterfaceMembers(superElement, memberName, paramCount, originalMember, result, visited);
            }
        }

        private static string GetImplementationNote(IDeclaredElement element)
        {
            if (!(element is IOverridableMember overridable)) return null;

            var superMembers = overridable.GetImmediateSuperMembers();
            if (superMembers == null) return null;

            var notes = new List<string>();
            foreach (var superMemberInstance in superMembers)
            {
                var superMember = superMemberInstance.Member;
                if (superMember == null) continue;
                notes.Add(PsiHelpers.GetQualifiedName(superMember));
            }

            if (notes.Count == 0) return null;

            return "Note: This is an implementation/override of " +
                   string.Join(", ", notes) +
                   ". Use find_usages on the interface/base member to find call sites.";
        }

        private class RawUsage
        {
            public string Project;
            public string File;
            public int Line;
            public int Column;
            public string Text;
        }
    }
}
