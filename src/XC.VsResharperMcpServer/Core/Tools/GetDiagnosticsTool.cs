using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Application.Settings;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's GetDiagnosticsTool (see docs/DEVNOTES.md). Output reshaped from a
    // structured dictionary to a formatted string, matching the other M2 tools' plain-text convention
    // (the SDK's McpServerTool.Create wraps whatever the delegate returns as text content either way).
    public class GetDiagnosticsTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public GetDiagnosticsTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath, string minSeverity = "warning", int line = 0, int column = 0)
        {
            return PsiThreadDispatcher.ExecuteRead(_shellLocks, _solution, "XC.VsResharperMcpServer.get_diagnostics", () =>
                ExecuteCore(filePath, minSeverity, line, column));
        }

        private string ExecuteCore(string filePath, string minSeverityText, int posLine, int posColumn)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";

            var minSeverity = ParseSeverity(minSeverityText) ?? Severity.WARNING;
            var hasPosition = posLine > 0 && posColumn > 0;

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            int? positionOffset = null;
            if (hasPosition)
            {
                var document = sourceFile.Document;
                if (document != null)
                {
                    var docLine = (Int32<DocLine>)(posLine - 1);
                    var docColumn = (Int32<DocColumn>)(posColumn - 1);
                    positionOffset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
                }
            }

            IContextBoundSettingsStore settings;
            HighlightingSettingsManager mgr;
            try
            {
                settings = sourceFile.GetSettingsStoreWithEditorConfig(_solution);
                mgr = _solution.GetComponent<HighlightingSettingsManager>();
            }
            catch (Exception e)
            {
                return $"Failed to initialize daemon settings/components: {e.Message}";
            }

            var collected = DaemonHighlightingCollector.Collect(_solution, sourceFile, settings);

            var diagnostics = new List<(int line, int col, int endLine, int endCol, string severity, string id, string message, bool hasQuickFix)>();
            foreach (var info in collected)
            {
                var highlighting = info.Highlighting;
                if (highlighting == null) continue;

                Severity severity;
                try
                {
                    severity = mgr.GetSeverity(highlighting, sourceFile, _solution, settings);
                }
                catch
                {
                    continue;
                }

                if ((int)severity < (int)minSeverity) continue;

                var range = info.Range;
                if (!range.IsValid()) continue;

                if (positionOffset.HasValue)
                {
                    var start = range.StartOffset.Offset;
                    var end = range.EndOffset.Offset;
                    if (positionOffset.Value < start || positionOffset.Value > end)
                        continue;
                }

                var (startLine, startCol) = PsiHelpers.GetLineColumn(range.StartOffset);
                var (endLine, endCol) = PsiHelpers.GetLineColumn(range.EndOffset);

                string inspectionId = null;
                try
                {
                    inspectionId = highlighting.GetConfigurableSeverityId();
                }
                catch
                {
                    // Some highlightings have no configurable severity id.
                }

                bool hasQuickFix = DaemonHighlightingCollector.HasQuickFix(_solution, info);

                string message = null;
                try
                {
                    message = highlighting.ToolTip ?? highlighting.ErrorStripeToolTip;
                }
                catch
                {
                    // ToolTip can throw for some synthetic highlightings.
                }

                diagnostics.Add((startLine, startCol, endLine, endCol, SeverityToString(severity),
                    inspectionId ?? highlighting.GetType().Name, message, hasQuickFix));
            }

            var ordered = diagnostics.OrderBy(d => d.line).ThenBy(d => d.col).ToList();

            var sb = new StringBuilder();
            sb.Append(filePath).Append(" - ").Append(ordered.Count).AppendLine(" diagnostic(s)");

            if (collected.Count == 0)
            {
                sb.AppendLine("note: No highlightings were produced by the daemon stages for this file. " +
                    "This can happen when inspection stages do not run headlessly outside an editor session. " +
                    "Try get_file_errors for syntax errors and unresolved references.");
            }
            else if (ordered.Count == 0)
            {
                sb.Append("note: ").Append(collected.Count)
                  .Append(" highlighting(s) were produced but none met the minimum severity '")
                  .Append(SeverityToString(minSeverity)).Append('\'')
                  .AppendLine(positionOffset.HasValue ? " (or covered the requested position)." : ".");
            }

            foreach (var d in ordered)
            {
                sb.AppendLine();
                sb.Append(d.line).Append(':').Append(d.col).Append('-').Append(d.endLine).Append(':').Append(d.endCol)
                  .Append(' ').Append(d.severity).Append(" [").Append(d.id).Append(']');
                if (!string.IsNullOrEmpty(d.message))
                    sb.Append(' ').Append(d.message);
                if (d.hasQuickFix)
                    sb.Append(" (quick-fix available)");
            }

            return sb.ToString().TrimEnd();
        }

        private static Severity? ParseSeverity(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            switch (value.Trim().ToLowerInvariant())
            {
                case "error": return Severity.ERROR;
                case "warning": return Severity.WARNING;
                case "suggestion": return Severity.SUGGESTION;
                case "hint": return Severity.HINT;
                case "info": return Severity.INFO;
                default: return null;
            }
        }

        private static string SeverityToString(Severity severity)
        {
            switch (severity)
            {
                case Severity.ERROR: return "error";
                case Severity.WARNING: return "warning";
                case Severity.SUGGESTION: return "suggestion";
                case Severity.HINT: return "hint";
                case Severity.INFO: return "info";
                case Severity.DO_NOT_SHOW: return "none";
                default: return "unknown";
            }
        }
    }
}
