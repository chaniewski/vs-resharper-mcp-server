using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ProjectModel.Impl;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported near-verbatim from resharper-mcp's GetSolutionStructureTool (see docs/DEVNOTES.md).
    public class GetSolutionStructureTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetSolutionStructureTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(bool includeFiles = false)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_solution_structure", () =>
                ExecuteCore(includeFiles));
        }

        private string ExecuteCore(bool includeFiles)
        {
            var solutionPath = _solution.SolutionFilePath.FullPath;
            var sb = new StringBuilder();

            var projects = _solution.GetAllProjects()
                .Where(p => p.ProjectFileLocation != null && !p.ProjectFileLocation.IsEmpty)
                .ToList();

            sb.Append(solutionPath).Append(" - ").Append(projects.Count).AppendLine(" projects");

            foreach (var project in projects)
            {
                sb.AppendLine();
                sb.Append("[").Append(project.Name).Append("] ").AppendLine(project.ProjectFileLocation.FullPath);

                var tfms = project.TargetFrameworkIds;
                if (tfms != null && tfms.Any())
                    sb.Append("  frameworks: ").AppendLine(string.Join(", ", tfms.Select(t => t.ToString())));

                var projectRefs = new System.Collections.Generic.HashSet<string>();
                foreach (var tfm in project.TargetFrameworkIds ?? System.Linq.Enumerable.Empty<JetBrains.Util.Dotnet.TargetFrameworkIds.TargetFrameworkId>())
                {
                    foreach (var reference in project.GetProjectReferences(tfm))
                    {
                        var refName = ProjectReferenceExtension.GetReferencedName(reference);
                        if (!string.IsNullOrEmpty(refName))
                            projectRefs.Add(refName);
                    }
                }
                if (projectRefs.Count > 0)
                    sb.Append("  references: ").AppendLine(string.Join(", ", projectRefs.OrderBy(r => r)));

                var fileCount = project.GetAllProjectFiles().Count();
                sb.Append("  files: ").AppendLine(fileCount.ToString());

                if (includeFiles)
                {
                    foreach (var file in project.GetAllProjectFiles().OrderBy(f => f.Location.FullPath))
                    {
                        sb.Append("    ").AppendLine(file.Location.FullPath);
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}
