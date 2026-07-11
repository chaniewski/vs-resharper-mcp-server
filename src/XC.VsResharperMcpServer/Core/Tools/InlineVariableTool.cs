using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.InlineVar;
using JetBrains.TextControl;
using JetBrains.TextControl.DataContext;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M7 (not from the reference repo - see docs/DEVNOTES.md). Inline a local variable: replace
    // every read of it with its initializer expression and remove the declaration. The inverse of
    // extract-local. Uses the same synthetic-IDataContext technique proven by SafeDeleteTool -
    // InlineVarWorkflow has no protected DataModel-style setter either, only the standard public
    // Initialize(IDataContext)/CreateRefactoring(IRefactoringDriver) pair.
    //
    // Unlike SafeDelete, InlineVarWorkflow.Initialize ALSO requires a real ITextControl in the data
    // context (context.GetData<ITextControl>(TextControlDataConstants.TEXT_CONTROL) - hard `return
    // false` if null), on top of the declared element - found by decompiling the actual method body
    // (ilspycmd) after synthetic-context-only Initialize kept failing with no diagnostic signal (see
    // docs/DEVNOTES.md). The text control is only actually read later, to show an error tooltip on the
    // "no usages found"/analysis-failed paths - it's never touched on the success path - but the null
    // check up front is unconditional. ITextControlManager.CreateTextControl(Lifetime, IDocument) (a
    // [ShellComponent], DI-injectable like DataContexts) creates one directly from a document with no
    // open editor tab required, which is exactly what's needed here.
    public class InlineVariableTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly DataContexts _dataContexts;
        private readonly ITextControlManager _textControlManager;

        public InlineVariableTool(ISolution solution, IShellLocks shellLocks, DataContexts dataContexts,
            ITextControlManager textControlManager)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _dataContexts = dataContexts;
            _textControlManager = textControlManager;
        }

        public string Execute(
            string symbolName = null,
            string kind = null,
            string filePath = null,
            int line = 0,
            int column = 0,
            bool dryRun = false)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.inline_variable", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, dryRun));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column, bool dryRun)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var elementName = declaredElement.ShortName;

            var sourceFile = declaredElement.GetDeclarations()
                .Select(d => d.GetSourceFile())
                .FirstOrDefault(f => f != null);
            var document = sourceFile?.Document;
            if (document == null)
                return $"Could not find a source document for '{elementName}' - it may be from a compiled/external assembly.";

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

                var workflow = new InlineVarWorkflow(_solution, "XC.VsResharperMcpServer.InlineVariable")
                {
                    WorkflowExecuterLifetime = lifetime
                };

                ITextControl textControl;
                try
                {
                    textControl = _textControlManager.CreateTextControl(lifetime, document);
                }
                catch (System.Exception ex)
                {
                    return $"Could not create a text control for '{elementName}': {ex.Message}";
                }

                var elementRule = new DataRule<IDeclaredElement>(
                    "XC.VsResharperMcpServer.InlineVariable.Element",
                    RefactoringDataConstants.DeclaredElementWithoutSelection,
                    declaredElement);
                var textControlRule = new DataRule<ITextControl>(
                    "XC.VsResharperMcpServer.InlineVariable.TextControl",
                    TextControlDataConstants.TEXT_CONTROL,
                    textControl);
                var context = _dataContexts.CreateWithoutDataRules(lifetime, new IDataRule[] { elementRule, textControlRule });

                if (!workflow.Initialize(context))
                    return $"Symbol '{elementName}' cannot be inlined: not a local variable/constant, has no " +
                           "single initializer, or the refactoring reported it is unavailable here.";

                using (var transaction = dryRun
                    ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.inline_variable")
                    : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.inline_variable"))
                {
                    // Same DrivenRefactoringWorkflow lifecycle gap fixed in SafeDeleteTool: Initialize
                    // alone doesn't populate usage/analysis data - PreExecute does (confirmed by
                    // decompiling InlineVarWorkflow.PreExecute, which delegates to the base
                    // implementation after firing an analytics event).
                    if (!workflow.PreExecute(NullProgressIndicator.Create()))
                    {
                        var preConflicts = ExtractConflicts(driver);
                        transaction.Rollback();
                        return FormatResult(elementName, dryRun, false, preConflicts, declarationFiles) +
                               "\n\n(pre-execution step failed)";
                    }

                    var refactoring = new InlineVarRefactoring(workflow, _solution, driver);
                    bool executed = refactoring.Execute(NullProgressIndicator.Create());
                    workflow.PostExecute(NullProgressIndicator.Create());

                    var conflicts = ExtractConflicts(driver);

                    if (dryRun && conflicts.Count == 0 && !executed)
                        transaction.Rollback();

                    return FormatResult(elementName, dryRun, executed, conflicts, declarationFiles);
                }
            }
            finally
            {
                lifetimeDefinition.Terminate();
            }
        }

        private static string FormatResult(string elementName, bool dryRun, bool executed,
            List<(string message, string severity, bool isValid)> conflicts, List<string> declarationFiles)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append("inline ").Append(elementName)
              .Append(dryRun ? " (not applied)" : executed ? " (applied)" : " (NOT applied)").AppendLine();

            if (conflicts.Count > 0)
            {
                sb.AppendLine();
                sb.Append("conflicts (").Append(conflicts.Count).AppendLine("):");
                foreach (var c in conflicts)
                    sb.Append("  [").Append(c.severity).Append("] ").AppendLine(c.message);
            }

            if (declarationFiles.Count > 0)
            {
                sb.AppendLine();
                sb.Append(dryRun ? "would change " : "changed ").Append(declarationFiles.Count).AppendLine(" file(s):");
                foreach (var f in declarationFiles)
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
    }
}
