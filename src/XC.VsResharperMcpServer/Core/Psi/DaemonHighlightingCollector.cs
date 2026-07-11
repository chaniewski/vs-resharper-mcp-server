using System.Collections.Generic;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.Dependencies;

namespace XC.VsResharperMcpServer.Core.Psi
{
    // Ported near-verbatim from resharper-mcp (Rider) — see docs/DEVNOTES.md.
    // Drives ReSharper's daemon inspection engine headlessly over a single file via the
    // protected DaemonProcessBase.DoHighlighting orchestration (the supported way to do this
    // outside an interactive editor session — see DaemonTestImpl.RunHighlight for precedent).
    // Must be called on the ReSharper main thread under a read lock with documents committed.
    public static class DaemonHighlightingCollector
    {
        public static IList<HighlightingInfo> Collect(
            ISolution solution,
            IPsiSourceFile sourceFile,
            IContextBoundSettingsStore settings = null)
        {
            if (solution == null || sourceFile == null)
                return new List<HighlightingInfo>();

            if (settings == null)
            {
                try
                {
                    settings = sourceFile.GetSettingsStoreWithEditorConfig(solution);
                }
                catch
                {
                    return new List<HighlightingInfo>();
                }
            }

            CollectingDaemonProcess process;
            try
            {
                process = new CollectingDaemonProcess(sourceFile, settings);
            }
            catch
            {
                return new List<HighlightingInfo>();
            }

            try
            {
                process.RunHeadless();
            }
            catch
            {
                // A misbehaving stage must not crash the host: return whatever was collected so far.
            }

            return new List<HighlightingInfo>(process.Collected);
        }

        public static bool HasQuickFix(ISolution solution, HighlightingInfo info)
        {
            if (solution == null || info?.Highlighting == null)
                return false;

            try
            {
                var quickFixTable = solution.GetComponent<QuickFixTable>();
                var instances = quickFixTable.EnumerateAvailableQuickFixes(info);
                if (instances == null)
                    return false;

                foreach (var instance in instances)
                {
                    if (instance?.QuickFix != null)
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private sealed class CollectingDaemonProcess : DaemonProcessBase
        {
            public readonly List<HighlightingInfo> Collected = new List<HighlightingInfo>();

            public CollectingDaemonProcess(IPsiSourceFile sourceFile, IContextBoundSettingsStore settings)
                : base(sourceFile, null, settings)
            {
            }

            public void RunHeadless()
            {
                DoHighlighting(DaemonProcessKind.VISIBLE_DOCUMENT, Commit);
            }

            private void Commit(DaemonCommitContext ctx)
            {
                var toAdd = ctx?.HighlightingsToAdd;
                if (toAdd == null) return;
                foreach (var info in toAdd)
                {
                    if (info?.Highlighting != null)
                        Collected.Add(info);
                }
            }

            protected override bool RunStagesInParallel => false;

            public override bool FullRehighlightingRequired => true;

            public override bool IsRangeInvalidated(DocumentRange range) => true;

            protected override bool ShouldNotifySwea(IPsiSourceFile sourceFile) => false;

            protected override void AnalysisStageCompleted(
                IPsiSourceFile sourceFile,
                IDaemonStage stage,
                byte layer,
                List<HighlightingInfo> stageHighlightings,
                bool stageFullRehighlight,
                List<DocumentRange> stageRanges,
                DaemonProcessKind processKind,
                IContextBoundSettingsStore settingsStore)
            {
            }

            protected override void FilePartlyReanalyzed(
                IPsiSourceFile sourceFile,
                DaemonProcessBase daemonProcessBase,
                DaemonProcessKind processKind)
            {
            }

            protected override void AnalysisCompleted(
                IPsiSourceFile sourceFile,
                DaemonProcessBase daemonProcessBase,
                DependencySet dependencies,
                bool analysisSupported,
                DaemonProcessKind processKind)
            {
            }
        }
    }
}
