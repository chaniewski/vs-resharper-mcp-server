using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Progress;
using JetBrains.Application.Threading;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.Intentions.Scoped.Actions;
using JetBrains.ReSharper.Feature.Services.Intentions.Scoped.Scopes;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Transactions;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's ApplySuggestionsTool (see docs/DEVNOTES.md). Drives ReSharper's own
    // "Fix all in file" engine by inspection id - position-free, file-wide, complements
    // ApplyQuickFixTool (single position). Only scoped fixes (IModernManualScopedAction) can run this
    // way; others are reported as skipped. Self-transacting: the scoped executor manages its own
    // PSI transactions.
    //
    // List/dry-run paths (ExecuteReadOnly) are CONFIRMED FIXED after causing a real, reproducible
    // devenv.exe hang (root cause: DaemonHighlightingCollector requires a read lock, this tool used
    // to always dispatch via a write lock regardless of whether anything was being mutated). The
    // fix-applying path (originally ExecuteApply, one continuous write-lock-held loop) caused a
    // SECOND, separately-confirmed hang for the same underlying reason - restructured into
    // ExecuteApplyLoop, which alternates short-lived read-lock scans (FindNextPick) and write-lock
    // applies (ApplyPick) per iteration instead. See ExecuteApplyLoop's own doc comment and
    // docs/DEVNOTES.md - UNVERIFIED live as of this restructuring.
    public class ApplySuggestionsTool
    {
        private const int MaxFixTypesPerFile = 50;

        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ApplySuggestionsTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string filePath,
            string inspectionIds = null,
            bool all = false,
            bool dryRun = false)
        {
            // DaemonHighlightingCollector's own doc comment requires being called "under a read
            // lock" - a real, documented contract this tool used to violate for its read-only paths
            // (list mode with no inspectionIds/all, and dryRun) by always dispatching through
            // ExecuteSelfTransactingWrite (a WRITE lock) regardless of whether anything was actually
            // going to be mutated. That mismatch is the confirmed cause of a real, reproducible IDE
            // hang (list mode, called live, froze devenv.exe - see docs/DEVNOTES.md); the exact same
            // DaemonHighlightingCollector.Collect call succeeds instantly under get_diagnostics'
            // ExecuteRead dispatch on the identical file moments earlier, isolating the difference to
            // lock kind, not the collector itself. Only the actual fix-APPLYING path (idFilter != null
            // or all=true, with dryRun=false) performs real mutations and needs a write lock; list and
            // dry-run are read-only and now correctly use ExecuteRead instead.
            var idFilter = ParseCsv(inspectionIds);
            var isReadOnlyCall = (idFilter == null && !all) || dryRun;

            if (isReadOnlyCall)
            {
                return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_suggestions", () =>
                    ExecuteReadOnly(filePath, idFilter, all, dryRun));
            }

            return ExecuteApplyLoop(filePath, idFilter, all);
        }

        // Confirmed root cause of a second, real devenv.exe hang (this one specifically in the
        // fix-APPLYING path, not just list/dry-run): DaemonProcessBase.DoHighlighting (called by
        // DaemonHighlightingCollector.Collect) internally does `using (ReadLockCookie.Create(...))`
        // (decompiled directly). That succeeds fine when the outer dispatch already holds a read
        // lock (ExecuteRead, ExecuteReadOnly above) - but when the outer dispatch instead holds the
        // EXCLUSIVE WRITE lock (which is what the old single-dispatch ExecuteApply used, since
        // applying a fix genuinely needs write access), the same thread's own nested read-lock
        // acquisition attempt has nowhere to go: it can't be granted while its own write lock is
        // still held, and the thread can't release the write lock because it's the one blocked
        // waiting on the read-lock acquisition. A same-thread self-deadlock, not just a
        // multi-thread one - explains why it reproduced with a single specific inspectionId too
        // (fires on the very first Collect() call inside the old loop, before any real work).
        //
        // Fix: never hold the write lock while calling Collect. Each iteration is now two SEPARATE
        // top-level dispatches instead of one continuous write-lock-held loop: FindNextPick runs
        // under ExecuteRead (safe - matches get_diagnostics' proven-working dispatch), and only the
        // resulting Pick (if any) gets applied under a following, separate ExecuteSelfTransactingWrite
        // dispatch. The one residual, not-fully-certain assumption: the IHighlighting/
        // IModernManualScopedAction captured under the read dispatch is reused in the immediately-next
        // write dispatch, i.e. held briefly across a lock-release/reacquire boundary - nothing else
        // touches the PSI in between (this loop is the only writer here), so this should be safe in
        // practice, but isn't a documented-safe pattern the way staying within one dispatch would be.
        // UNVERIFIED beyond this reasoning until live-tested - see docs/DEVNOTES.md.
        private string ExecuteApplyLoop(string filePath, HashSet<string> idFilter, bool applyAll)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";

            var applied = new List<string>();
            var skipped = new HashSet<string>();
            var errors = new List<string>();
            var handledTypes = new HashSet<string>();
            var sawFileNotFound = false;

            for (var iteration = 0; iteration < MaxFixTypesPerFile; iteration++)
            {
                var pick = PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_suggestions.scan", () =>
                    FindNextPick(filePath, idFilter, applyAll, handledTypes, skipped, out sawFileNotFound));

                if (sawFileNotFound)
                    return $"File not found in solution: {filePath}";

                if (pick == null)
                    break;

                handledTypes.Add(pick.TypeName);

                var applyResult = PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_suggestions.apply", () =>
                    ApplyPick(filePath, pick));

                if (applyResult.error != null)
                    errors.Add($"{pick.InspectionId ?? "(no id)"}: {applyResult.error}");
                else if (!applyResult.changed)
                    errors.Add($"{pick.InspectionId ?? "(no id)"}: fix reported success but did not change the file");
                else
                    applied.Add($"{pick.InspectionId ?? "(no id)"} - \"{pick.FixText}\"");
            }

            string persistError = null;
            if (applied.Count > 0)
            {
                persistError = PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_suggestions.persist", () =>
                {
                    try
                    {
                        var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
                        var text = sourceFile?.Document?.GetText();
                        if (text != null)
                            PsiHelpers.PersistDocumentToDisk(filePath, text);
                        return (string)null;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                });
            }

            return FormatResult(filePath, applied, skipped, errors, persistError);
        }

        // Runs under ExecuteRead - must not mutate anything. Finds the first not-yet-handled
        // matching fix, or null if there's nothing left to apply.
        private Pick FindNextPick(string filePath, HashSet<string> idFilter, bool applyAll,
            HashSet<string> handledTypes, HashSet<string> skipped, out bool fileNotFound)
        {
            fileNotFound = false;
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
            {
                fileNotFound = true;
                return null;
            }

            var settingsManager = _solution.GetComponent<HighlightingSettingsManager>();
            var quickFixTable = _solution.GetComponent<QuickFixTable>();

            foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
            {
                var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                if (!Matches(inspectionId, idFilter, applyAll))
                    continue;

                foreach (var instance in EnumerateFixes(quickFixTable, info))
                {
                    if (!(instance.QuickFix is IModernManualScopedAction scoped))
                    {
                        if (inspectionId != null)
                            skipped.Add(inspectionId);
                        continue;
                    }

                    var typeName = instance.QuickFix.GetType().FullName;
                    if (handledTypes.Contains(typeName))
                        continue;

                    return new Pick
                    {
                        Scoped = scoped,
                        Highlighting = info.Highlighting,
                        InspectionId = inspectionId,
                        TypeName = typeName,
                        FixText = FixText(instance)
                    };
                }
            }

            return null;
        }

        // Runs under ExecuteSelfTransactingWrite. Applies the one already-located Pick; does NOT
        // call DaemonHighlightingCollector/Collect - that's the whole point of the split (see
        // ExecuteApplyLoop's comment).
        private (bool changed, string error) ApplyPick(string filePath, Pick pick)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return (false, "file no longer found in solution");

            var psiServices = _solution.GetPsiServices();
            var textBefore = sourceFile.Document?.GetText();
            try
            {
                // IModernManualScopedAction.ExecuteAction's own interface doc says "This method
                // is executed in document transaction" - a precondition, not a guarantee. Decompiled
                // the real UI caller (ModernManualScopedActionInstance.GetCommandSequence) and
                // confirmed it wraps the call via BulbActionCommands.DocumentChange(...) before
                // running it - this tool never opened any PSI transaction at all, unlike every
                // other write tool in this codebase, which explains the historical "reports
                // applied but makes no real progress" bug (see docs/DEVNOTES.md).
                using (PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(psiServices, "XC.VsResharperMcpServer.apply_suggestions"))
                {
                    pick.Scoped.ExecuteAction(
                        _solution, new SourceFileScope(sourceFile), pick.Highlighting,
                        NullProgressIndicator.Create());
                }

                var textAfter = sourceFile.Document?.GetText();
                return (!string.Equals(textBefore, textAfter, StringComparison.Ordinal), null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private string ExecuteReadOnly(string filePath, HashSet<string> idFilter, bool applyAll, bool dryRun)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";

            if (idFilter == null && !applyAll)
                return ListApplicable(filePath);

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var settingsManager = _solution.GetComponent<HighlightingSettingsManager>();
            var quickFixTable = _solution.GetComponent<QuickFixTable>();

            return DescribeDryRun(filePath, sourceFile, settingsManager, quickFixTable, idFilter, applyAll);
        }

        private string ListApplicable(string filePath)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var settingsManager = _solution.GetComponent<HighlightingSettingsManager>();
            var quickFixTable = _solution.GetComponent<QuickFixTable>();
            var ids = ApplicableIds(sourceFile, settingsManager, quickFixTable);

            var sb = new StringBuilder();
            sb.Append(filePath).AppendLine(" - specify 'inspectionIds' or pass all=true.");
            if (ids.Count > 0)
            {
                sb.AppendLine().AppendLine("applicable inspection ids in this file:");
                foreach (var id in ids.OrderBy(x => x))
                    sb.Append("  ").AppendLine(id);
            }
            else
            {
                sb.AppendLine().AppendLine("no applicable (scoped) suggestions found.");
            }

            return sb.ToString().TrimEnd();
        }

        private string DescribeDryRun(
            string filePath, IPsiSourceFile sourceFile, HighlightingSettingsManager settingsManager,
            QuickFixTable quickFixTable, HashSet<string> idFilter, bool applyAll)
        {
            var wouldApply = new List<string>();
            var seenTypes = new HashSet<string>();
            foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
            {
                var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                if (!Matches(inspectionId, idFilter, applyAll))
                    continue;

                foreach (var instance in EnumerateFixes(quickFixTable, info))
                {
                    if (!(instance.QuickFix is IModernManualScopedAction))
                        continue;
                    if (!seenTypes.Add(instance.QuickFix.GetType().FullName))
                        continue;
                    wouldApply.Add($"{inspectionId ?? "(no id)"} - \"{FixText(instance)}\"");
                }
            }

            if (wouldApply.Count == 0)
                return $"{filePath} - dry run: nothing to apply";

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - dry run: would apply ").Append(wouldApply.Count).AppendLine(" fix type(s):");
            foreach (var entry in wouldApply)
                sb.Append("  ").AppendLine(entry);
            return sb.ToString().TrimEnd();
        }

        private HashSet<string> ApplicableIds(
            IPsiSourceFile sourceFile, HighlightingSettingsManager settingsManager, QuickFixTable quickFixTable)
        {
            var ids = new HashSet<string>();
            foreach (var info in DaemonHighlightingCollector.Collect(_solution, sourceFile))
            {
                var inspectionId = GetInspectionId(settingsManager, info.Highlighting);
                if (inspectionId == null)
                    continue;
                foreach (var instance in EnumerateFixes(quickFixTable, info))
                {
                    if (instance.QuickFix is IModernManualScopedAction)
                    {
                        ids.Add(inspectionId);
                        break;
                    }
                }
            }

            return ids;
        }

        private static IEnumerable<QuickFixInstance> EnumerateFixes(QuickFixTable quickFixTable, HighlightingInfo info)
        {
            if (info?.Highlighting == null)
                return Enumerable.Empty<QuickFixInstance>();
            try
            {
                var instances = quickFixTable.EnumerateAvailableQuickFixes(info);
                return instances == null ? Enumerable.Empty<QuickFixInstance>() : instances.Where(i => i?.QuickFix != null).ToList();
            }
            catch (Exception)
            {
                return Enumerable.Empty<QuickFixInstance>();
            }
        }

        private static string GetInspectionId(HighlightingSettingsManager settingsManager, IHighlighting highlighting)
        {
            if (highlighting == null)
                return null;
            if (highlighting is ICustomConfigurableSeverityIdHighlighting custom &&
                !string.IsNullOrEmpty(custom.ConfigurableSeverityId))
                return custom.ConfigurableSeverityId;

            var attribute = settingsManager.GetHighlightingAttribute(highlighting);
            return (attribute as ConfigurableSeverityHighlightingAttribute)?.ConfigurableSeverityId;
        }

        private static bool Matches(string inspectionId, HashSet<string> idFilter, bool applyAll)
        {
            if (applyAll)
                return true;
            return inspectionId != null && idFilter.Contains(inspectionId);
        }

        private static string FixText(QuickFixInstance instance)
        {
            if (instance.QuickFix is IBulbAction bulb && !string.IsNullOrEmpty(bulb.Text))
                return bulb.Text;
            return instance.QuickFix.GetType().Name;
        }

        private static string FormatResult(string filePath, List<string> applied, HashSet<string> skipped, List<string> errors, string persistError = null)
        {
            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - applied ").Append(applied.Count).AppendLine(" fix type(s)");

            if (applied.Count > 0)
            {
                sb.AppendLine();
                foreach (var entry in applied)
                    sb.Append("  + ").AppendLine(entry);
            }

            if (persistError != null)
            {
                sb.AppendLine();
                sb.Append("WARNING: failed to persist changes to disk: ").AppendLine(persistError);
            }

            if (skipped.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("skipped (not headlessly applicable - try apply_quick_fix at a position):");
                foreach (var id in skipped.OrderBy(x => x))
                    sb.Append("  ").AppendLine(id);
            }

            if (errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("errors:");
                foreach (var error in errors)
                    sb.Append("  ").AppendLine(error);
            }

            return sb.ToString().TrimEnd();
        }

        private static HashSet<string> ParseCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return null;
            return new HashSet<string>(
                csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        private sealed class Pick
        {
            public IModernManualScopedAction Scoped;
            public IHighlighting Highlighting;
            public string InspectionId;
            public string TypeName;
            public string FixText;
        }
    }
}
