using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Refactorings.Conflicts;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.ReSharper.Refactorings.ChangeSignature;
using JetBrains.ReSharper.Refactorings.CSharp.ChangeSignature;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in M7 (not from the reference repo - see docs/DEVNOTES.md). Reorder/remove parameters and
    // change the return type of a method/constructor/indexer, updating all call sites.
    //
    // Unlike Safe Delete/Inline Variable, Change Signature does NOT go through an
    // Initialize(IDataContext) workflow at all - CSharpChangeSignature.CreateModel(IParametersOwner,
    // IProgressIndicator) builds a ClrChangeSignatureModel directly from the target element, no
    // synthetic IDataContext needed. The model exposes list-like primitives (Add/RemoveAt/MoveTo) over
    // its ChangeSignatureParameters array, and ChangeSignatureRefactoring(model) is executed directly
    // (its Execute(IProgressIndicator) is void - no built-in "did it run" signal like the other
    // refactorings' Execute returns - so conflicts are checked via GetConflictSearcher() first and
    // Execute() is only actually invoked when no Error-severity conflict is present).
    //
    // v1 scope: reorder existing parameters, remove existing parameters, retype existing parameters,
    // change the return type. Adding brand-new parameters is NOT supported yet (would need to
    // construct an IParameterValue default-value expression for existing call sites - deferred).
    // Renaming a parameter is already covered by rename_symbol (parameters are declared elements too).
    public class ChangeSignatureTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ChangeSignatureTool(ISolution solution, IShellLocks shellLocks)
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
            int[] parameterOrder = null,
            string[] parameterTypes = null,
            string newReturnType = null,
            bool dryRun = false)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.change_signature", () =>
                ExecuteCore(symbolName, kind, filePath, line, column, parameterOrder, parameterTypes, newReturnType, dryRun));
        }

        private string ExecuteCore(string symbolName, string kind, string filePath, int line, int column,
            int[] parameterOrder, string[] parameterTypes, string newReturnType, bool dryRun)
        {
            var (declaredElement, error) = PsiHelpers.ResolveFromArgs(_solution, symbolName, kind, filePath, line, column);
            if (error != null) return error;

            var owner = declaredElement as IParametersOwner;
            if (owner == null)
                return $"Symbol '{declaredElement.ShortName}' is not a method, constructor, indexer, or " +
                       "delegate - change_signature only applies to parameterized members.";

            var elementName = declaredElement.ShortName;

            var declarationFiles = declaredElement.GetDeclarations()
                .Select(d => d.GetSourceFile()?.GetLocation().FullPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            var changeSignature = new CSharpChangeSignature(CSharpLanguage.Instance);
            if (!changeSignature.IsAvailable(owner))
                return $"Change Signature is not available for '{elementName}' here.";

            var psiServices = _solution.GetPsiServices();

            using (var transaction = dryRun
                ? PsiTransactionCookie.CreateTemporaryChangeCookie(psiServices, "XC.VsResharperMcpServer.change_signature")
                : PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.change_signature"))
            {
                var model = changeSignature.CreateModel(owner, NullProgressIndicator.Create());

                var applyError = ApplyParameterChanges(model, parameterOrder, parameterTypes);
                if (applyError != null)
                {
                    transaction.Rollback();
                    return applyError;
                }

                if (!string.IsNullOrEmpty(newReturnType))
                    model.ReturnTypeName = newReturnType;

                var refactoring = new ChangeSignatureRefactoring(model);
                var searchResult = refactoring.GetConflictSearcher().SearchConflicts(NullProgressIndicator.Create(), false);
                var conflicts = ExtractConflicts(searchResult?.Conflicts);
                var blocked = conflicts.Any(c => c.severity == ConflictSeverity.Error.ToString() && c.isValid);

                bool executed = false;
                if (!dryRun && !blocked)
                {
                    refactoring.Execute(NullProgressIndicator.Create());
                    executed = true;
                }

                if (dryRun || blocked)
                    transaction.Rollback();

                return FormatResult(elementName, dryRun, executed, blocked, conflicts, declarationFiles);
            }
        }

        // parameterOrder holds ORIGINAL parameter indices, in the desired final order; an original
        // index omitted from the array is removed. Applied as: remove first (highest index first, so
        // earlier indices stay valid), then move each kept parameter into its target position, then
        // retype in place. Returns an error string, or null on success.
        private static string ApplyParameterChanges(ClrChangeSignatureModel model, int[] parameterOrder, string[] parameterTypes)
        {
            if (parameterOrder == null || parameterOrder.Length == 0)
                return null;

            if (parameterTypes != null && parameterTypes.Length != parameterOrder.Length)
                return "'parameterTypes', if provided, must have the same length as 'parameterOrder'.";

            var originalCount = model.ChangeSignatureParameters.Length;
            foreach (var idx in parameterOrder)
            {
                if (idx < 0 || idx >= originalCount)
                    return $"'parameterOrder' contains {idx}, which is out of range for a signature with {originalCount} original parameter(s).";
            }
            if (parameterOrder.Distinct().Count() != parameterOrder.Length)
                return "'parameterOrder' contains duplicate indices.";

            var keepSet = new HashSet<int>(parameterOrder);
            for (var i = model.ChangeSignatureParameters.Length - 1; i >= 0; i--)
            {
                if (!keepSet.Contains(model.ChangeSignatureParameters[i].OriginalParameterIndex))
                    model.RemoveAt(i);
            }

            for (var targetPos = 0; targetPos < parameterOrder.Length; targetPos++)
            {
                var origIdx = parameterOrder[targetPos];
                var currentParams = model.ChangeSignatureParameters;
                var currentPos = -1;
                for (var j = 0; j < currentParams.Length; j++)
                {
                    if (currentParams[j].OriginalParameterIndex == origIdx) { currentPos = j; break; }
                }
                if (currentPos < 0)
                    return $"Internal error: could not locate original parameter {origIdx} after removal step.";
                if (currentPos != targetPos)
                    model.MoveTo(currentPos, targetPos);
            }

            if (parameterTypes != null)
            {
                for (var i = 0; i < parameterTypes.Length; i++)
                {
                    if (!string.IsNullOrEmpty(parameterTypes[i]))
                        model.ChangeSignatureParameters[i].TypeName = parameterTypes[i];
                }
            }

            return null;
        }

        private static string FormatResult(string elementName, bool dryRun, bool executed, bool blocked,
            List<(string message, string severity, bool isValid)> conflicts, List<string> declarationFiles)
        {
            var sb = new StringBuilder();
            sb.Append(dryRun ? "[dry run] " : "").Append("change signature of ").Append(elementName)
              .Append(dryRun ? " (not applied)" : executed ? " (applied)" : blocked ? " (NOT applied - blocked by conflicts)" : " (NOT applied)")
              .AppendLine();

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

        private static List<(string message, string severity, bool isValid)> ExtractConflicts(IEnumerable<IConflict> conflicts)
        {
            var result = new List<(string, string, bool)>();
            if (conflicts == null) return result;
            foreach (var conflict in conflicts)
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
