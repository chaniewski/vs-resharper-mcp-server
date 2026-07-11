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
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.apply_quick_fix", () =>
                ExecuteCore(filePath, line, column, fixId, index));
        }

        private string ExecuteCore(string filePath, int line, int column, string fixId, int index)
        {
            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return "Provide 'filePath' + 'line' + 'column' (1-based)";

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var document = sourceFile.Document;
            if (document == null)
                return "Could not get document for file";

            int offset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                offset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
            }
            catch (Exception ex)
            {
                return $"Could not resolve position {line}:{column}: {ex.Message}";
            }

            List<Candidate> candidates;
            try
            {
                candidates = CollectCandidates(sourceFile, offset);
            }
            catch (Exception ex)
            {
                return $"Failed to collect quick-fixes: {ex.Message}";
            }

            if (candidates.Count == 0)
                return "No quick-fixes are available at this position. The file may not have been analyzed " +
                       "yet (open it in the editor so the daemon computes highlightings), or there is no issue here.";

            var availableText = string.Join("; ", candidates.Select((c, i) => $"[{i}] {c.Text}"));

            Candidate chosen = null;

            if (!string.IsNullOrEmpty(fixId))
            {
                chosen = candidates.FirstOrDefault(c => string.Equals(c.Text, fixId, StringComparison.Ordinal))
                    ?? candidates.FirstOrDefault(c => string.Equals(c.Text, fixId, StringComparison.OrdinalIgnoreCase));

                if (chosen == null)
                    return $"No available fix matches fixId '{fixId}'. Available: {availableText}";
            }
            else if (index >= 0)
            {
                if (index >= candidates.Count)
                    return $"index {index} is out of range (0..{candidates.Count - 1}). Available: {availableText}";
                chosen = candidates[index];
            }
            else if (candidates.Count == 1)
            {
                chosen = candidates[0];
            }
            else
            {
                return $"{candidates.Count} fixes available; specify 'fixId' or 'index' to apply one. Available: {availableText}";
            }

            return ApplyFix(chosen, document, filePath);
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

                        var text = SafeText(actionInstance, bulbAction);
                        if (string.IsNullOrEmpty(text)) continue;

                        var key = highlightingId + "|" + text;
                        if (!seen.Add(key)) continue;

                        result.Add(new Candidate { Text = text, HighlightingId = highlightingId, BulbAction = bulbAction });
                    }
                }
            }

            return result;
        }

        private string ApplyFix(Candidate chosen, IDocument document, string filePath)
        {
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

        private static string SafeText(IntentionActionInstance actionInstance, IBulbAction bulbAction)
        {
            try
            {
                var rich = actionInstance.RichText;
                if (rich != null)
                {
                    var t = rich.Text;
                    if (!string.IsNullOrEmpty(t)) return t;
                }
            }
            catch
            {
                // fall through to bulb action text
            }

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
        }
    }
}
