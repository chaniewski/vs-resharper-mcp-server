using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.QuickFixes;
using JetBrains.ReSharper.Psi;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's ListQuickFixesTool (see docs/DEVNOTES.md), reshaped from a
    // structured dictionary to a formatted string. Read-only: never mutates the PSI or documents.
    public class ListQuickFixesTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public ListQuickFixesTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath, int line, int column)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.list_quick_fixes", () =>
                ExecuteCore(filePath, line, column));
        }

        private string ExecuteCore(string filePath, int line, int column)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";
            if (line <= 0 || column <= 0)
                return "Provide 'filePath' plus 1-based 'line' and 'column'";

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var document = sourceFile.Document;
            if (document == null)
                return "Could not get document for file";

            int positionOffset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                positionOffset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
            }
            catch (Exception e)
            {
                return $"Invalid position {line}:{column}: {e.Message}";
            }

            IContextBoundSettingsStore settings;
            QuickFixTable quickFixTable;
            try
            {
                settings = sourceFile.GetSettingsStoreWithEditorConfig(_solution);
                quickFixTable = _solution.GetComponent<QuickFixTable>();
            }
            catch (Exception e)
            {
                return $"Failed to initialize daemon settings/components: {e.Message}";
            }

            var collected = DaemonHighlightingCollector.Collect(_solution, sourceFile, settings);

            var atPosition = new List<HighlightingInfo>();
            foreach (var info in collected)
            {
                var range = info.Range;
                if (!range.IsValid()) continue;

                var start = range.StartOffset.Offset;
                var end = range.EndOffset.Offset;
                if (positionOffset < start || positionOffset > end) continue;

                atPosition.Add(info);
            }

            var fixes = new List<(string fixId, string text, string quickFixType, string highlightingType)>();
            var seenFixes = new HashSet<string>();

            foreach (var highlightingInfo in atPosition)
            {
                var highlighting = highlightingInfo.Highlighting;
                if (highlighting == null) continue;

                var highlightingType = highlighting.GetType().FullName ?? highlighting.GetType().Name;

                IEnumerable<QuickFixInstance> instances;
                try
                {
                    instances = quickFixTable.EnumerateAvailableQuickFixes(highlightingInfo);
                }
                catch
                {
                    continue;
                }

                if (instances == null) continue;

                foreach (var instance in instances)
                {
                    if (instance?.QuickFix == null) continue;

                    var fixType = instance.QuickFix.GetType();
                    var quickFixType = fixType.FullName ?? fixType.Name;
                    var fixId = fixType.Name;

                    IReadOnlyList<JetBrains.ReSharper.Feature.Services.Intentions.IntentionActionInstance> actionInstances;
                    try
                    {
                        actionInstances = instance.CreateActionInstances(_solution);
                    }
                    catch
                    {
                        continue;
                    }

                    if (actionInstances == null) continue;

                    foreach (var actionInstance in actionInstances)
                    {
                        if (actionInstance == null) continue;

                        string text = null;
                        try
                        {
                            text = actionInstance.BulbAction?.Text;
                        }
                        catch
                        {
                            // Some bulb actions compute Text lazily and may throw; skip the text.
                        }

                        var dedupKey = $"{highlightingType}|{quickFixType}|{text}";
                        if (!seenFixes.Add(dedupKey)) continue;

                        fixes.Add((fixId, text, quickFixType, highlightingType));
                    }
                }
            }

            var ordered = fixes
                .OrderBy(f => f.text ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sb = new StringBuilder();
            sb.Append(filePath).Append(':').Append(line).Append(':').Append(column)
              .Append(" - ").Append(ordered.Count).AppendLine(" quick-fix(es)");

            if (collected.Count == 0)
                sb.AppendLine("note: No highlightings were produced by the daemon stages for this file. " +
                    "This can happen when inspection stages do not run headlessly outside an editor session.");
            else if (atPosition.Count == 0)
                sb.Append("note: ").Append(collected.Count)
                  .Append(" highlighting(s) were produced for this file but none cover ")
                  .Append(line).Append(':').Append(column).AppendLine(".");
            else if (ordered.Count == 0)
                sb.Append("note: Highlighting(s) cover ").Append(line).Append(':').Append(column)
                  .AppendLine(" but no quick-fixes are available for them.");

            foreach (var f in ordered)
            {
                sb.AppendLine();
                sb.Append(f.fixId).Append(": ").AppendLine(f.text ?? "(no text)");
                sb.Append("  quickFixType: ").AppendLine(f.quickFixType);
                sb.Append("  highlightingType: ").AppendLine(f.highlightingType);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
