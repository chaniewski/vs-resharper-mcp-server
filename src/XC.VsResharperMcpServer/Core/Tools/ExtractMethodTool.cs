using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.CSharp.ExtractMethod2.Common;
using JetBrains.ReSharper.Refactorings.CSharp.ExtractMethod2.FromStatements;
using JetBrains.ReSharper.Refactorings.ExtractMethod2;
using JetBrains.Util;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M7 (not from the reference repo - see docs/DEVNOTES.md). Extract a range of statements
    // into a new method (or property/local function/chained constructor, when ReSharper itself would
    // also offer those as options for the same selection). Originally written and reasoned through
    // entirely via decompilation (ilspycmd against the real installed
    // JetBrains.ReSharper.Refactorings.CSharp.dll) during an autonomous unsupervised session with no VS
    // instance available.
    //
    // LIVE-TESTED (2026-07-12, see docs/DEVNOTES.md): dry run and real apply both confirmed correct on
    // a real two-statement extraction (parameter/return-value inference both right, call site correctly
    // rewritten, 0 diagnostics on the result) - but only after a real bug was found and fixed. The very
    // first live attempt threw a NullReferenceException deep inside the SDK's own call-site
    // reformatting step (not this tool's code) because no IModuleReferenceResolveContext was
    // established on this headless dispatch thread - fixed centrally in PsiThreadDispatcher via
    // CompilationContextCookie.GetExplicitUniversalContextIfNotSet(), which benefits every tool, not
    // just this one. See PsiThreadDispatcher.cs's own doc comment for the full root-cause writeup.
    //
    // Unlike rename_symbol/change_signature/inline_variable, this does NOT go through
    // Initialize(IDataContext) at all - CSharpExtractMethodFromStatementsWorkflow.IsAvailable has a
    // SECOND, non-UI overload that takes an ICSharpStatementsRange directly
    // (StatementUtil.GetStatementsRange(solution, documentRange) - no ITextControl/selection needed),
    // bypassing the synthetic-IDataContext technique entirely. This is a genuinely different, simpler
    // shape than the other three refactoring tools, found by decompiling
    // CSharpExtractMethodFromStatementsWorkflow directly rather than assuming the same
    // Initialize(IDataContext) pattern would apply.
    //
    // CSharpExtractMethodWorkflowBase.Model has a PROTECTED setter (can't be assigned from this tool
    // directly, only from a subclass) - worked around with a tiny local subclass
    // (HeadlessExtractMethodWorkflow) whose only job is exposing a way to assign it. The interactive
    // flow normally builds Model then shows a UI page (ExtractMethodPage) offering a popup to choose
    // among available "occurrences" (extract as Method vs Property vs Local Function vs Chained
    // Constructor, whichever the selection supports) before ever touching Model - replicated headlessly
    // by calling GetOccurrences(analysisResult) directly and picking one via the 'kind' parameter
    // (default: prefer Method, matching the plan's "turn a selected statement range into a new method"
    // framing) instead of driving SelectOccurrenceBehaviour's UI popup.
    //
    // The RefineControlFlowAnalysis/InitializeNaming/CoerceParameterNames sequence below is copied
    // verbatim from CSharpExtractMethodWorkflowBase.FirstPendingRefactoringPage's page-factory lambda
    // (decompiled) - that lambda normally runs right before showing the interactive "configure extracted
    // method" wizard page, and appears to be where the control-flow analysis gets refined against the
    // actual target site (not just the constructor's initial pass) - skipping it looked like the likeliest
    // way to end up with an incorrect parameter list, so it's replicated here even though nothing in this
    // codebase can exercise it live yet.
    //
    // v1 scope: extracting a STATEMENT RANGE only (CSharpExtractMethodFromStatementsWorkflow). NOT
    // implemented: extracting a single expression as a new method
    // (JetBrains.ReSharper.Refactorings.CSharp.ExtractMethod2.FromExpression - a sibling workflow with
    // its own IsAvailable/Runner pair, not investigated this round) and Extract Method Object
    // (ExtractedEntityKind.MethodObject, which GetOccurrences never appears to surface for a plain
    // statement-range selection based on the decompiled source - only Method/Property/LocalFunction/
    // ChainedConstructor are ever added to the occurrence list there).
    public class ExtractMethodTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ExtractMethodTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string filePath,
            int startLine,
            int startColumn,
            int endLine,
            int endColumn,
            string newMethodName = null,
            string kind = null,
            bool dryRun = false)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.extract_method", () =>
                ExecuteCore(filePath, startLine, startColumn, endLine, endColumn, newMethodName, kind, dryRun));
        }

        private string ExecuteCore(string filePath, int startLine, int startColumn, int endLine, int endColumn,
            string newMethodName, string kind, bool dryRun)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var document = sourceFile.Document;
            if (document == null)
                return $"Could not find a document for '{filePath}'.";

            int startOffset, endOffset;
            try
            {
                startOffset = GetOffset(document, startLine, startColumn);
                endOffset = GetOffset(document, endLine, endColumn);
            }
            catch (Exception ex)
            {
                return $"Invalid range: {ex.Message}";
            }

            if (endOffset <= startOffset)
                return "'endLine'/'endColumn' must be after 'startLine'/'startColumn'.";

            var documentRange = new DocumentRange(document, new TextRange(startOffset, endOffset));
            var statementsRange = StatementUtil.GetStatementsRange(_solution, documentRange);
            if (statementsRange == null)
                return "Could not resolve a valid statement range for the given span - selection must " +
                       "cover one or more complete statements.";

            var lifetimeDefinition = new LifetimeDefinition();
            try
            {
                var lifetime = lifetimeDefinition.Lifetime;

                var workflow = new HeadlessExtractMethodWorkflow(_solution, "XC.VsResharperMcpServer.ExtractMethod")
                {
                    WorkflowExecuterLifetime = lifetime
                };

                if (!workflow.IsAvailable(statementsRange))
                    return "Extract Method is not available for this statement range (not inside a member, " +
                           "or the selection can't be safely extracted).";

                var analysisResult = workflow.AnalyzeDataFlow();
                if (analysisResult == null)
                    return "Control-flow analysis failed for this statement range - cannot extract.";

                var occurrences = workflow.GetOccurrences(analysisResult);
                if (occurrences.Length == 0)
                    return "No extractable occurrence found for this statement range.";

                var chosen = PickOccurrence(occurrences, kind);
                if (chosen == null)
                    return $"Requested kind '{kind}' is not available here. Available: " +
                           string.Join(", ", occurrences.Select(o => o.EntityKind.ToString()));

                var psiSourceFile = analysisResult.OwnerDeclaration.GetSourceFile();
                if (psiSourceFile == null)
                    return "Could not resolve the source file that owns the selected statements.";

                var model = new CSharpExtractMethodModel(workflow, analysisResult, workflow.SelectedRange,
                    workflow.TargetSiteContext, psiSourceFile, chosen.EntityKind);
                workflow.AssignModel(model);

                // Matches CSharpExtractMethodWorkflowBase.FirstPendingRefactoringPage's page-factory
                // lambda (decompiled) - see class doc comment.
                model.RefineControlFlowAnalysis(model.TargetSiteContext);
                model.InitializeNaming();
                model.AnalysisResult.CoerceParameterNames(model.TargetSiteContext);

                if (!string.IsNullOrEmpty(newMethodName))
                    model.MethodName = newMethodName;

                var methodName = model.MethodName;
                var psiServices = _solution.GetPsiServices();
                var storage = new RefactoringDriverStorage();
                var driver = new RefactoringDriverWithConflicts(storage);

                using (var transaction = dryRun
                    ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.extract_method")
                    : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.extract_method"))
                {
                    if (!workflow.PreExecute(NullProgressIndicator.Create()))
                    {
                        var preConflicts = ExtractConflicts(driver);
                        transaction.Rollback();
                        return FormatResult(methodName, chosen.EntityKind.ToString(), filePath, dryRun, false, preConflicts) +
                               "\n\n(pre-execution step failed)";
                    }

                    var refactoring = workflow.CreateRefactoring(driver);
                    bool executed = refactoring.Execute(NullProgressIndicator.Create());
                    workflow.PostExecute(NullProgressIndicator.Create());

                    var conflicts = ExtractConflicts(driver);

                    if (dryRun && conflicts.Count == 0 && !executed)
                        transaction.Rollback();

                    return FormatResult(methodName, chosen.EntityKind.ToString(), filePath, dryRun, executed, conflicts);
                }
            }
            finally
            {
                lifetimeDefinition.Terminate();
            }
        }

        private static ExtractMethodPopupOccurrence PickOccurrence(ExtractMethodPopupOccurrence[] occurrences, string kind)
        {
            if (string.IsNullOrEmpty(kind))
                return occurrences.FirstOrDefault(o => o.EntityKind == ExtractedEntityKind.Method) ?? occurrences[0];

            ExtractedEntityKind requested;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "method": requested = ExtractedEntityKind.Method; break;
                case "property": requested = ExtractedEntityKind.Property; break;
                case "local-function":
                case "localfunction": requested = ExtractedEntityKind.LocalFunction; break;
                case "chained-constructor":
                case "chainedconstructor": requested = ExtractedEntityKind.ChainedConstructor; break;
                default: return null;
            }

            return occurrences.FirstOrDefault(o => o.EntityKind == requested);
        }

        // Same line/column -> document-offset conversion PsiHelpers.GetNodeAtPosition uses internally,
        // duplicated here (not extracted to PsiHelpers) since it needs to throw on out-of-range input
        // rather than silently returning a clamped/wrong offset - this tool's range comes directly from
        // caller-supplied coordinates, not a single already-valid position.
        private static int GetOffset(IDocument document, int line, int column)
        {
            var docLine = (Int32<DocLine>)(line - 1);
            var docColumn = (Int32<DocColumn>)(column - 1);
            var coords = new DocumentCoords(docLine, docColumn);
            return document.GetOffsetByCoords(coords);
        }

        private static string FormatResult(string methodName, string entityKind, string filePath, bool dryRun,
            bool executed, List<(string message, string severity, bool isValid)> conflicts)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append("extract ").Append(entityKind.ToLowerInvariant())
              .Append(" '").Append(methodName).Append('\'')
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
                sb.Append(dryRun ? "would change " : "changed ").AppendLine("file:");
                sb.Append("  ").AppendLine(filePath);
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

        // CSharpExtractMethodWorkflowBase.Model has a protected setter (see class doc comment) - this
        // subclass exists purely to expose AssignModel so ExecuteCore can populate it without an
        // interactive Initialize(IDataContext) call.
        private sealed class HeadlessExtractMethodWorkflow : CSharpExtractMethodFromStatementsWorkflow
        {
            public HeadlessExtractMethodWorkflow(ISolution solution, string actionId) : base(solution, actionId)
            {
            }

            public void AssignModel(CSharpExtractMethodModel model)
            {
                Model = model;
            }
        }
    }
}
