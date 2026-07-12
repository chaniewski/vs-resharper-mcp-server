using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Bulbs;
using JetBrains.ReSharper.Feature.Services.Intentions;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.TextControl;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's ApplyQuickFixTool (see docs/DEVNOTES.md).
    // RISKY / best-effort: executing a quick-fix headlessly genuinely needs a UI/main thread and an
    // ITextControl. Self-transacting because IBulbAction.Execute manages its own transactions - the
    // host must not wrap it in an outer one. Every failure degrades to a graceful "not applied" +
    // reason rather than throwing, matching the reference's defensive posture.
    //
    // PSI-LOCK WEDGE, root-caused and fixed 2026-07-12 (see docs/DEVNOTES.md "apply_quick_fix PSI-lock
    // wedge" entry). Two quick-fix families - JetBrains.ReSharper.Intentions.CSharp.QuickFixes.
    // CreateFromUsageFix (every "Create parameter/field/property/local/etc. from usage" fix) and the
    // RenameWrongRefFixBase family (RenameLocalWrongRefFix, RenameWrongRefFix) - were confirmed via
    // decompilation to always finish their real work by kicking off an ASYNC, interactive live-template
    // "hotspot" session (CSharpTemplateUtil.ExecuteTemplate / BulbActionCommands.ShowHotspotSession,
    // scheduled through BulbActionExecutor.RunEntryPointOrContinueAfterAsync's
    // IAsyncAfterDocumentTransactionBulbActionCommand branch) that expects a real user to type into and
    // dismiss it. Nothing ever does in a headless dispatch, so the shared JetBrains ReentrancyGuard/
    // primary-thread dispatcher effectively never considers that action "finished" - every subsequent
    // PSI-lock-dependent call from ANY tool (not just this one) then queues forever behind it, and only
    // a full devenv.exe restart clears it. The PSI mutation itself (e.g. the parameter/rename groundwork)
    // completes fine before the hotspot step starts; it's specifically the hotspot session that never
    // returns. HeadlessUnsafeQuickFixes.BlockedTypes (shared with ListQuickFixesTool) refuses these
    // BEFORE ever calling Execute, converting a session-wide wedge into an immediate, clean "not
    // supported headless" response - the only reliable mitigation available from managed code, since the
    // hang happens deep inside SDK-owned scheduling we can't safely cancel or run off-thread (PSI access
    // is thread-affinitized to the same primary thread every other tool call needs). Any future quick-fix
    // type discovered to hang the same way should be added there rather than re-diagnosed from scratch -
    // the tell is the same: ExecutePsiTransaction returns a delegate that ends up calling
    // ExecuteTemplate/ShowHotspotSession instead of doing its work synchronously.
    //
    // TWO ADDITIONAL ROUNDS were needed to actually make this safe, both found live: (1) a fourth
    // hang-prone type, ImportTypeFromNuGetFix (searches nuget.org, needs network + interactive
    // selection) - added to the blocklist. (2) more seriously, even a no-index, purely-informational
    // call (candidate listing only, never reaching Execute) could STILL hang the whole plugin under the
    // original single-dispatch design, because collection itself ran under ExecuteSelfTransactingWrite
    // (a write lock) even though it never mutates anything. RichText access was suspected and removed
    // first (SafeText below) but did not fix it alone. Execute is now split into two dispatches: a
    // read-lock SELECTION phase (position resolution, candidate collection, choosing one, the blocklist
    // check) and, only if a specific non-blocked candidate was chosen, a write-lock APPLY phase. This
    // mirrors ListQuickFixesTool's dispatch (ExecuteRead) exactly for the collection work, which has
    // never hung doing the same enumeration. The exact mechanism of why write-lock-context collection
    // hangs was not fully confirmed via decompilation (unlike the two root causes above) - this fix is
    // empirically verified (repeated live tests, no hang) rather than mechanistically proven, and is
    // recorded as such rather than overclaiming certainty.
    public class ApplyQuickFixTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ApplyQuickFixTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(
            string filePath,
            int line,
            int column,
            string fixId = null,
            int index = -1)
        {
            // Two-phase dispatch, split 2026-07-12 (see docs/DEVNOTES.md "apply_quick_fix PSI-lock
            // wedge"). Originally this whole method - selection AND apply - ran under a single
            // ExecuteSelfTransactingWrite (write-lock) dispatch. Live testing found that even a
            // no-index, purely-informational call (which never reaches ApplyFix/Execute at all) could
            // hang the whole plugin under that dispatch, while ListQuickFixesTool's near-identical
            // candidate collection - which only ever runs under ExecuteRead - never has. RichText access
            // was ruled out as the cause first (removed entirely, no change); the write-lock-vs-read-lock
            // difference is the next most likely explanation and costs nothing to fix defensively:
            // collecting/selecting candidates never mutates anything, so it never needed a write lock in
            // the first place. Only phase 2 (the actual apply, for one specific already-vetted candidate)
            // still needs ExecuteSelfTransactingWrite, since IBulbAction.Execute manages its own PSI
            // transaction. The Candidate/IDocument captured in phase 1 are handed to phase 2 across the
            // lock boundary - a small window where the underlying PSI could theoretically change under a
            // concurrent request, but any staleness there degrades to the existing try/catch around
            // chosen.BulbAction.Execute(...) rather than a hang, which is a strictly better failure mode
            // than what this replaces.
            SelectionResult selection;
            try
            {
                selection = PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_quick_fix.select", () =>
                    SelectCandidate(filePath, line, column, fixId, index));
            }
            catch (Exception ex)
            {
                return $"Tool 'XC.VsResharperMcpServer.apply_quick_fix' failed during selection: {ex}";
            }

            if (selection.Message != null)
                return selection.Message;

            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_quick_fix.apply", () =>
                ApplyFix(selection.Chosen, selection.Document, filePath));
        }

        private SelectionResult SelectCandidate(string filePath, int line, int column, string fixId, int index)
        {
            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return SelectionResult.WithMessage("Provide 'filePath' + 'line' + 'column' (1-based)");

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return SelectionResult.WithMessage($"File not found in solution: {filePath}");

            var document = sourceFile.Document;
            if (document == null)
                return SelectionResult.WithMessage("Could not get document for file");

            int offset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                offset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
            }
            catch (Exception ex)
            {
                return SelectionResult.WithMessage($"Could not resolve position {line}:{column}: {ex.Message}");
            }

            List<Candidate> candidates;
            try
            {
                candidates = CollectCandidates(sourceFile, offset);
            }
            catch (Exception ex)
            {
                return SelectionResult.WithMessage($"Failed to collect quick-fixes: {ex.Message}");
            }

            if (candidates.Count == 0)
                return SelectionResult.WithMessage(
                    "No quick-fixes are available at this position. The file may not have been analyzed " +
                    "yet (open it in the editor so the daemon computes highlightings), or there is no issue here.");

            var availableText = string.Join("; ", candidates.Select((c, i) =>
                $"[{i}] {c.Text}" + (HeadlessUnsafeQuickFixes.IsBlocked(c.QuickFixTypeName)
                    ? " (NOT SUPPORTED HEADLESS - would hang, see docs/DEVNOTES.md)"
                    : "")));

            Candidate chosen;

            if (!string.IsNullOrEmpty(fixId))
            {
                chosen = candidates.FirstOrDefault(c => string.Equals(c.Text, fixId, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault(c => string.Equals(c.Text, fixId, StringComparison.OrdinalIgnoreCase));

                if (chosen == null)
                    return SelectionResult.WithMessage($"No available fix matches fixId '{fixId}'. Available: {availableText}");
            }
            else if (index >= 0)
            {
                if (index >= candidates.Count)
                    return SelectionResult.WithMessage($"index {index} is out of range (0..{candidates.Count - 1}). Available: {availableText}");
                chosen = candidates[index];
            }
            else if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                return SelectionResult.WithMessage($"{candidates.Count} fixes available; specify 'fixId' or 'index' to apply one. Available: {availableText}");
            }

            if (HeadlessUnsafeQuickFixes.IsBlocked(chosen.QuickFixTypeName))
            {
                return SelectionResult.WithMessage(
                    $"'{chosen.Text}' NOT applied - this fix ('{chosen.QuickFixTypeName}') is known to hang " +
                    "indefinitely when run headlessly: its real work finishes by opening an interactive " +
                    "live-template/hotspot session (to let a human type/confirm a name or type) that never " +
                    "gets dismissed outside a real editor. Refused before execution to avoid wedging this " +
                    "plugin's PSI lock for every other tool until devenv.exe is restarted - see " +
                    "docs/DEVNOTES.md 'apply_quick_fix PSI-lock wedge'. Use rename_symbol, generate_members, " +
                    "or change_signature instead where applicable.");
            }

            return SelectionResult.WithChosen(chosen, document);
        }

        private List<Candidate> CollectCandidates(IPsiSourceFile sourceFile, int offset)
        {
            var result = new List<Candidate>();
            var seen = new HashSet<string>();

            var quickFixTable = _solution.GetComponent<QuickFixTable>();
            if (quickFixTable == null)
                return result;

            var collected = DaemonHighlightingCollector.Collect(_solution, sourceFile);

            foreach (var highlightingInfo in collected)
            {
                if (highlightingInfo?.Highlighting == null) continue;

                var range = highlightingInfo.Range;
                if (!range.IsValid()) continue;
                if (offset < range.StartOffset.Offset || offset > range.EndOffset.Offset) continue;

                IEnumerable<QuickFixInstance> fixInstances;
                try
                {
                    fixInstances = quickFixTable.EnumerateAvailableQuickFixes(highlightingInfo);
                }
                catch
                {
                    continue;
                }

                if (fixInstances == null) continue;

                var highlightingId = highlightingInfo.Highlighting.GetType().Name;

                foreach (var fixInstance in fixInstances)
                {
                    if (fixInstance == null) continue;

                    var quickFixTypeName = fixInstance.QuickFix?.GetType().FullName;

                    IReadOnlyList<IntentionActionInstance> actionInstances;
                    try
                    {
                        actionInstances = fixInstance.CreateActionInstances(_solution);
                    }
                    catch
                    {
                        continue;
                    }

                    if (actionInstances == null) continue;

                    foreach (var actionInstance in actionInstances)
                    {
                        var bulbAction = actionInstance?.BulbAction;
                        if (bulbAction == null) continue;

                        var text = SafeText(bulbAction);
                        if (string.IsNullOrEmpty(text)) continue;

                        var key = highlightingId + "|" + text;
                        if (!seen.Add(key)) continue;

                        result.Add(new Candidate
                        {
                            Text = text,
                            HighlightingId = highlightingId,
                            BulbAction = bulbAction,
                            QuickFixTypeName = quickFixTypeName,
                        });
                    }
                }
            }

            return result;
        }

        private string ApplyFix(Candidate chosen, IDocument document, string filePath)
        {
            // The HeadlessUnsafeQuickFixes blocklist check already happened in SelectCandidate (phase 1,
            // under a read lock) before this write-lock phase was ever dispatched - not repeated here.

            ITextControl textControl = null;
            LifetimeDefinition syntheticLifetime = null;
            var usedSyntheticControl = false;

            try
            {
                textControl = TryGetOpenTextControl(document);

                if (textControl == null)
                {
                    try
                    {
                        var tcManager = _solution.GetComponent<ITextControlManager>();
                        if (tcManager != null)
                        {
                            syntheticLifetime = Lifetime.Define(_solution.GetLifetime(), "XC.VsResharperMcpServer.ApplyQuickFix");
                            textControl = tcManager.CreateTextControl(syntheticLifetime.Lifetime, document);
                            usedSyntheticControl = textControl != null;
                        }
                    }
                    catch (Exception ex)
                    {
                        return $"'{chosen.Text}' NOT applied - requires interactive editor / not supported headless: " +
                               $"could not create a text control ({ex.Message})";
                    }
                }

                if (textControl == null)
                    return $"'{chosen.Text}' NOT applied - requires interactive editor / not supported headless: no text control available";

                var before = SafeGetText(document);

                try
                {
                    chosen.BulbAction.Execute(_solution, textControl);
                }
                catch (Exception ex)
                {
                    return $"'{chosen.Text}' NOT applied - requires interactive editor / not supported headless: " +
                           $"the fix could not run ({ex.GetType().Name}: {ex.Message})";
                }

                var after = SafeGetText(document);
                var changed = before == null || after == null || !string.Equals(before, after, StringComparison.Ordinal);

                var persisted = true;
                string persistError = null;
                if (changed && after != null)
                {
                    try
                    {
                        PsiHelpers.PersistDocumentToDisk(filePath, after);
                    }
                    catch (Exception ex)
                    {
                        persisted = false;
                        persistError = ex.Message;
                    }
                }

                var sb = new StringBuilder();
                sb.Append('\'').Append(chosen.Text).Append("' applied to ").Append(filePath);
                if (usedSyntheticControl) sb.Append(" (via synthetic editor)");
                sb.Append(changed ? " - file changed" : " - no textual change detected");
                if (changed && !persisted)
                    sb.Append(" (WARNING: failed to persist to disk: ").Append(persistError).Append(')');
                return sb.ToString();
            }
            finally
            {
                try { syntheticLifetime?.Terminate(); }
                catch { /* best effort */ }
            }
        }

        private ITextControl TryGetOpenTextControl(IDocument document)
        {
            try
            {
                var tcManager = _solution.GetComponent<ITextControlManager>();
                if (tcManager == null) return null;

                foreach (var tc in tcManager.TextControls)
                {
                    if (tc != null && ReferenceEquals(tc.Document, document))
                        return tc;
                }
            }
            catch
            {
                // ignore - fall back to synthetic control
            }

            return null;
        }

        // Deliberately does NOT touch IntentionActionInstance.RichText - found live 2026-07-12 (see
        // docs/DEVNOTES.md "apply_quick_fix PSI-lock wedge") that reading RichText during candidate
        // COLLECTION (before ever calling Execute, before the HeadlessUnsafeQuickFixes blocklist check
        // even runs) could itself hang the whole plugin for at least one fix provider
        // (ImportTypeFromNuGetFix, whose "rich" description plausibly does a live nuget.org lookup to
        // build itself). ListQuickFixesTool only ever reads plain BulbAction.Text for the exact same
        // candidates and has never hung doing so - mirror that proven-safe path exactly rather than
        // trying to selectively preserve RichText only for not-yet-known-bad types.
        private static string SafeText(IBulbAction bulbAction)
        {
            try
            {
                return bulbAction.Text;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetText(IDocument document)
        {
            try
            {
                return document.GetText();
            }
            catch
            {
                return null;
            }
        }

        private class Candidate
        {
            public string Text;
            public string HighlightingId;
            public IBulbAction BulbAction;
            public string QuickFixTypeName;
        }

        private class SelectionResult
        {
            public string Message;
            public Candidate Chosen;
            public IDocument Document;

            public static SelectionResult WithMessage(string message) => new SelectionResult { Message = message };

            public static SelectionResult WithChosen(Candidate chosen, IDocument document) =>
                new SelectionResult { Chosen = chosen, Document = document };
        }
    }
}
