using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.DataContext;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.Move.MoveTypeDeclarationToFile;
using JetBrains.ReSharper.Resources.Shell;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M7 (not from the reference repo - see docs/DEVNOTES.md). Move a type declaration to its
    // own file (ReSharper's "Move to Another File" refactoring). NOT LIVE-TESTED - written and reasoned
    // through entirely via decompilation (ilspycmd against the real installed
    // JetBrains.ReSharper.NewRefactorings.dll / JetBrains.ReSharper.Refactorings.CSharp.dll), during an
    // autonomous unsupervised session with no VS instance available to run it against. Compiles clean;
    // behavior is unverified.
    //
    // Uses the same synthetic-IDataContext technique as InlineVariableTool/SafeDeleteTool
    // (MoveToFileWorkflow.Initialize(IDataContext) is a real, standard Initialize/PreExecute/Execute/
    // PostExecute DrivenRefactoringWorkflow2<T> lifecycle - no protected-setter workaround needed here,
    // unlike ExtractMethodTool). The one non-obvious part, found only by decompiling THROUGH two more
    // layers than usual (MoveToFileWorkflow.IsAvailableInternal -> MoveToFileHelperBase.GetTypeDeclaration
    // -> RefactoringWorkflowUtil.GetTypeDeclaration<T,T>): the actual declared-element lookup reads BOTH
    // PsiDataConstants.DECLARED_ELEMENTS (a collection - for the workflow's own initial availability
    // check) AND the separate, singular PsiDataConstants.DECLARED_ELEMENT (for the deeper helper's
    // lookup) - two different named DataConstants for what is conceptually the same value, not one.
    //
    // PsiDataConstants.DECLARED_ELEMENT is marked [Obsolete("Use DataConstants.DECLARED_ELEMENTS")] with
    // an explicit doc comment saying "You MUST NOT create data rules for this constant" - that warning
    // is written for the platform's NORMAL editor data-context pipeline, where a registered derived-rule
    // provider is presumably expected to compute DECLARED_ELEMENT automatically from DECLARED_ELEMENTS
    // and a manual rule would conflict with it. This tool's synthetic context
    // (DataContexts.CreateWithoutDataRules) has no such derived-rule pipeline wired in at all, so
    // without supplying DECLARED_ELEMENT explicitly too, RefactoringWorkflowUtil.GetTypeDeclaration's
    // context.GetData(PsiDataConstants.DECLARED_ELEMENT) read would just return null and the whole
    // refactoring would report "not available" - supplying it directly is deliberate, not an oversight
    // of the "MUST NOT" warning. This is the one part of this tool most worth re-checking first if it
    // turns out not to work: if DECLARED_ELEMENT genuinely can't be supplied this way even in a
    // synthetic context, the fallback would be reflecting into RefactoringWorkflowUtil or finding
    // whatever real derived-rule provider computes it normally.
    //
    // The other two PROJECT_MODEL_ELEMENT/TEXT_CONTROL branches inside
    // RefactoringWorkflowUtil.GetTypeDeclaration are deliberately left unpopulated (not needed) - with
    // both absent, that method correctly falls through to its own last-resort fallback
    // (declaredElement.GetDeclarations()[0]), which is exactly the declaration this tool already
    // resolved via PsiHelpers.ResolveFromArgs.
    //
    // v1 scope: move a type into its own new file (default name = the type's own name, overridable via
    // 'newFileName'). Does NOT change the type's namespace to match the new file's folder (ReSharper's
    // own MoveDeclaration keeps the original namespace - confirmed via decompiling
    // CSharpMoveToFileHelper.MoveDeclaration - namespace/folder sync is a separate, unrelated concern).
    // Deleting/renaming the now-possibly-empty original file is opt-in via 'removeOldFileIfEmpty'
    // (default false, matching ReSharper's own DataModel.RemoveOldFile default when driven
    // non-interactively) - a safer default for a first, unverified cut of this tool.
    public class MoveTypeTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly DataContexts _dataContexts;

        public MoveTypeTool(ISolution solution, IShellLocks shellLocks, DataContexts dataContexts)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _dataContexts = dataContexts;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            string newFileName = null,
            bool removeOldFileIfEmpty = false,
            bool dryRun = false)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.move_type", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, newFileName, removeOldFileIfEmpty, dryRun));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column,
            string newFileName, bool removeOldFileIfEmpty, bool dryRun)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            if (!(declaredElement is ITypeElement))
                return $"Symbol '{declaredElement.ShortName}' is not a type - move_type only applies to " +
                       "classes/structs/interfaces/enums/delegates.";

            var elementName = declaredElement.ShortName;
            var originalFile = declaredElement.GetDeclarations()
                .Select(d => d.GetSourceFile()?.GetLocation().FullPath)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p));

            var psiServices = _solution.GetPsiServices();
            var storage = new RefactoringDriverStorage();
            var driver = new RefactoringDriverWithConflicts(storage);

            var lifetimeDefinition = new LifetimeDefinition();
            try
            {
                var lifetime = lifetimeDefinition.Lifetime;

                var popupContext = Shell.Instance.GetComponent<IMainWindowPopupWindowContext>();
                var workflow = new MoveToFileWorkflow(_solution, "XC.VsResharperMcpServer.MoveType", popupContext)
                {
                    WorkflowExecuterLifetime = lifetime
                };

#pragma warning disable CS0618 // PsiDataConstants.DECLARED_ELEMENT is Obsolete - see class doc comment.
                var elementsRule = new DataRule<ICollection<IDeclaredElement>>(
                    "XC.VsResharperMcpServer.MoveType.Elements",
                    PsiDataConstants.DECLARED_ELEMENTS,
                    new List<IDeclaredElement> { declaredElement });
                var elementRule = new DataRule<IDeclaredElement>(
                    "XC.VsResharperMcpServer.MoveType.Element",
                    PsiDataConstants.DECLARED_ELEMENT,
                    declaredElement);
#pragma warning restore CS0618
                var context = _dataContexts.CreateWithoutDataRules(lifetime, new IDataRule[] { elementsRule, elementRule });

                if (!workflow.Initialize(context))
                    return $"Move Type is not available for '{elementName}' here (not a top-level-movable " +
                           "type, its file isn't part of a project, or the containing language isn't supported).";

                if (!string.IsNullOrEmpty(newFileName))
                    workflow.DataModel.NewFileName = newFileName + workflow.DataModel.NewFileExtension;

                workflow.DataModel.RemoveOldFile = removeOldFileIfEmpty;

                using (var transaction = dryRun
                    ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.move_type")
                    : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.move_type"))
                {
                    if (!workflow.PreExecute(NullProgressIndicator.Create()))
                    {
                        var preConflicts = ExtractConflicts(driver);
                        transaction.Rollback();
                        return FormatResult(elementName, originalFile, workflow.DataModel.NewFileName, dryRun, false, preConflicts) +
                               "\n\n(pre-execution step failed)";
                    }

                    var refactoring = workflow.CreateRefactoring(driver);
                    bool executed = refactoring.Execute(NullProgressIndicator.Create());
                    workflow.PostExecute(NullProgressIndicator.Create());

                    var conflicts = ExtractConflicts(driver);

                    if (dryRun && conflicts.Count == 0 && !executed)
                        transaction.Rollback();

                    return FormatResult(elementName, originalFile, workflow.DataModel.NewFileName, dryRun, executed, conflicts);
                }
            }
            finally
            {
                lifetimeDefinition.Terminate();
            }
        }

        private static string FormatResult(string elementName, string originalFile, string newFileName,
            bool dryRun, bool executed, List<(string message, string severity, bool isValid)> conflicts)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append("move type '").Append(elementName).Append('\'')
              .Append(dryRun ? " (not applied)" : executed ? " (applied)" : " (NOT applied)").AppendLine();

            if (conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.Append("conflicts (").Append(conflicts.Count).AppendLine("):");
                foreach (var c in conflicts)
                    sb.Append("  [").Append(c.severity).Append("] ").AppendLine(c.message);
            }

            if (executed || dryRun)
            {
                sb.AppendLine();
                sb.Append(dryRun ? "would move from " : "moved from ").AppendLine(originalFile ?? "(unknown)");
                sb.Append(dryRun ? "  to (new file name) " : "  to ").AppendLine(newFileName ?? "(unknown)");
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
    }
}
