using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Feature.Services.Refactorings.Specific.Rename;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.Rename;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's RenameSymbolTool (see docs/DEVNOTES.md) — safe, semantic,
    // solution-wide rename driven by the real ReSharper rename machinery (RenameWorkflow +
    // RenameRefactoring over a RenameDataModel). Self-transacting: opens its own transaction so it
    // can choose auto-commit (real rename) vs. temporary/rollback (dryRun) at the point of use, which
    // the host's ExecuteSelfTransactingWrite dispatch deliberately does not do for us.
    public class RenameSymbolTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public RenameSymbolTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string newName,
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            bool dryRun = false)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.rename_symbol", () =>
                ExecuteCore(newName, symbolName, kind, filePath, line, column, dryRun));
        }

        private string ExecuteCore(string newName, string symbolName, string kind, string filePath, int line, int column, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return "'newName' is required and must be a non-empty string.";

            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var oldName = declaredElement.ShortName;

            if (string.Equals(oldName, newName, StringComparison.Ordinal))
                return $"New name is identical to the current name ('{oldName}'); nothing to rename.";

            var factory = new AtomicRenamesFactory();
            var availability = factory.CheckRenameAvailability(declaredElement);
            if (availability != RenameAvailabilityCheckResult.CanBeRenamed)
                return $"Symbol '{oldName}' cannot be renamed: {availability}.";

            var declarationFiles = declaredElement.GetDeclarations()
                .Select(d => d.GetSourceFile()?.GetLocation().FullPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            var psiServices = _solution.GetPsiServices();

            var storage = new RefactoringDriverStorage();
            var driver = new RefactoringDriverWithConflicts(storage);

            var lifetimeDefinition = new LifetimeDefinition();
            try
            {
                var lifetime = lifetimeDefinition.Lifetime;

                var workflow = new HeadlessRenameWorkflow(_solution, "XC.VsResharperMcpServer.Rename")
                {
                    WorkflowExecuterLifetime = lifetime
                };

                var languageType = RenameUtil.GetPsiLanguageTypeOrKnownLanguage(declaredElement);
                var renameHelper = workflow.LanguageSpecific[languageType];
                if (renameHelper == null)
                    return "Rename is not supported for this symbol's language.";

                var model = renameHelper.GetOptionsModel(declaredElement, null, lifetime);

                var dataModel = new RenameDataModel(
                    new[] { declaredElement },
                    null,
                    RenameFilesOption.NothingToRename,
                    lifetime,
                    _solution,
                    model,
                    workflow)
                {
                    NewName = newName
                };

                // IMPORTANT: QuickRename must stay false — it short-circuits the solution-wide usage
                // search in LoadBaseRenames, meant for inline/local rename where usages are trivial.
                model.HasUI = false;
                model.QuickRename = false;
                model.RenameDerived = false;
                model.RenameFile = false;
                model.ChangeTextOccurrences = false;

                workflow.AssignDataModel(dataModel);

                using (var transaction = dryRun
                    ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.rename_symbol")
                    : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.rename_symbol"))
                {
                    dataModel.NameHasChanged = true;
                    dataModel.LoadBaseRenames(NullProgressIndicator.Create(), workflow, forceReload: true);

                    if (dataModel.AllRenames.Renames.Count == 0)
                    {
                        transaction.Rollback();
                        return $"No applicable renames were produced for '{oldName}'.";
                    }

                    var refactoring = new RenameRefactoring(workflow, _solution, driver);
                    bool executed = refactoring.Execute(NullProgressIndicator.Create());

                    var conflicts = ExtractConflicts(driver);
                    var changedFiles = CollectChangedFiles(dataModel, declarationFiles);

                    return FormatResult(oldName, newName, dryRun, executed, conflicts, changedFiles);
                }
            }
            finally
            {
                lifetimeDefinition.Terminate();
            }
        }

        private static string FormatResult(string oldName, string newName, bool dryRun, bool executed,
            List<(string message, string severity, bool isValid)> conflicts, List<string> changedFiles)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append(oldName).Append(" -> ").Append(newName)
              .Append(dryRun ? " (not applied)" : executed ? " (applied)" : " (NOT applied)").AppendLine();

            if (conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.Append("conflicts (").Append(conflicts.Count).AppendLine("):");
                foreach (var c in conflicts)
                    sb.Append("  [").Append(c.severity).Append("] ").AppendLine(c.message);
            }

            if (changedFiles.Count > 0)
            {
                sb.AppendLine();
                sb.Append(dryRun ? "would change " : "changed ").Append(changedFiles.Count).AppendLine(" file(s):");
                foreach (var f in changedFiles)
                    sb.Append("  ").AppendLine(f);
            }

            return sb.ToString().TrimEnd();
        }

        private static List<(string message, string severity, bool isValid)> ExtractConflicts(RefactoringDriverWithConflicts driver)
        {
            var result = new List<(string, string, bool)>();
            foreach (var conflict in driver.Conflicts)
            {
                if (conflict == null) continue;
                result.Add((SafeDescription(conflict), conflict.Severity.ToString(), SafeIsValid(conflict)));
            }
            return result;
        }

        private static string SafeDescription(IConflict conflict)
        {
            try { return conflict.Description; }
            catch { return "(conflict description unavailable)"; }
        }

        private static bool SafeIsValid(IConflict conflict)
        {
            try { return conflict.IsValid; }
            catch { return false; }
        }

        private static List<string> CollectChangedFiles(RenameDataModel dataModel, List<string> declarationFiles)
        {
            var files = new HashSet<string>(declarationFiles ?? new List<string>());

            try
            {
                foreach (var atomic in dataModel.AllRenames.Renames)
                {
                    var primary = atomic.PrimaryDeclaredElement;
                    if (primary == null) continue;

                    try
                    {
                        foreach (var refsInFile in dataModel.GetGroupedElementReferences(primary))
                        {
                            var path = refsInFile.SourceFile?.GetLocation().FullPath;
                            if (!string.IsNullOrEmpty(path))
                                files.Add(path);
                        }
                    }
                    catch
                    {
                        // Best-effort: a single atomic's reference enumeration failing should not
                        // sink the whole report.
                    }
                }
            }
            catch
            {
                // Ignore - declaration files alone are still a useful answer.
            }

            return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        private sealed class HeadlessRenameWorkflow : RenameWorkflow
        {
            public HeadlessRenameWorkflow(ISolution solution, string actionId)
                : base(solution, actionId)
            {
            }

            public void AssignDataModel(RenameDataModel dataModel) => DataModel = dataModel;
        }
    }
}
