using System;
using System.Collections.Generic;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCompletion;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.AspectLookupItems.BaseInfrastructure;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.AspectLookupItems.Info;
using JetBrains.ReSharper.Feature.Services.CodeCompletion.Infrastructure.LookupItems;
using JetBrains.ReSharper.Psi;
using JetBrains.TextControl;
using JetBrains.Util.dataStructures.TypedIntrinsics;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's CompleteAtTool (see docs/DEVNOTES.md). Logically READ-ONLY (no PSI
    // writes) but dispatched via ExecuteSelfTransactingWrite solely to get main-thread dispatch - the
    // completion engine asserts the R# main thread, and that dispatch is only available on the write
    // path. RISKY / best-effort: completion may legitimately return nothing outside an interactive
    // editing session; every failure degrades gracefully rather than throwing.
    public class CompleteAtTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public CompleteAtTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath, int line, int column, int maxResults = 50)
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.complete_at", () =>
                ExecuteCore(filePath, line, column, maxResults));
        }

        private string ExecuteCore(string filePath, int line, int column, int maxResults)
        {
            if (maxResults <= 0) maxResults = 50;

            if (string.IsNullOrEmpty(filePath) || line <= 0 || column <= 0)
                return "Provide 'filePath' plus 1-based 'line' and 'column'";

            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var document = sourceFile.Document;
            if (document == null)
                return $"No document available for file: {filePath}";

            int offset;
            try
            {
                var docLine = (Int32<DocLine>)(line - 1);
                var docColumn = (Int32<DocColumn>)(column - 1);
                offset = document.GetOffsetByCoords(new DocumentCoords(docLine, docColumn));
            }
            catch (Exception ex)
            {
                return $"Invalid position {line}:{column}: {ex.Message}";
            }

            const string unavailableNote = "completion unavailable headless / needs interactive session";

            try
            {
                ITextControlManager textControlManager;
                IntellisenseManager intellisenseManager;
                try
                {
                    textControlManager = _solution.GetComponent<ITextControlManager>();
                    intellisenseManager = _solution.GetComponent<IntellisenseManager>();
                }
                catch (Exception ex)
                {
                    return $"{filePath}:{line}:{column} - {unavailableNote} ({ex.Message})";
                }

                if (textControlManager == null || intellisenseManager == null)
                    return $"{filePath}:{line}:{column} - {unavailableNote}";

                string result = null;

                Lifetime.Using(lt =>
                {
                    ITextControl textControl;
                    try
                    {
                        textControl = textControlManager.CreateTextControl(lt, document);
                    }
                    catch (Exception ex)
                    {
                        result = $"{filePath}:{line}:{column} - {unavailableNote} ({ex.Message})";
                        return;
                    }

                    if (textControl == null)
                    {
                        result = $"{filePath}:{line}:{column} - {unavailableNote}";
                        return;
                    }

                    try
                    {
                        textControl.Caret.MoveTo(offset, CaretVisualPlacement.DontScrollIfVisible);
                    }
                    catch
                    {
                        // Non-fatal: completion uses the caret position but a move failure should not crash.
                    }

                    ICodeCompletionResult completionResult;
                    try
                    {
                        var parameters = new CodeCompletionParameters(CodeCompletionType.BasicCompletion);
                        completionResult = intellisenseManager.GetCompletionResult(parameters, textControl);
                    }
                    catch (Exception ex)
                    {
                        result = $"{filePath}:{line}:{column} - {unavailableNote} ({ex.Message})";
                        return;
                    }

                    result = ShapeResult(filePath, line, column, completionResult, maxResults);
                });

                return result ?? $"{filePath}:{line}:{column} - {unavailableNote}";
            }
            catch (Exception ex)
            {
                return $"{filePath}:{line}:{column} - {unavailableNote} ({ex.Message})";
            }
        }

        private static string GetKind(ILookupItem lookupItem)
        {
            var element = TryGetDeclaredElement(lookupItem);
            if (element != null)
            {
                if (element is IMethod method && method.IsExtensionMethod)
                    return "ExtensionMethod";

                var presentableName = element.GetElementType()?.PresentableName;
                if (!string.IsNullOrEmpty(presentableName))
                    return presentableName;
            }

            return CleanTypeName(lookupItem.GetType().Name);
        }

        private static IDeclaredElement TryGetDeclaredElement(ILookupItem lookupItem)
        {
            if (lookupItem is IDeclaredElementLookupItem declaredElementItem)
            {
                IDeclaredElement element = declaredElementItem.PreferredDeclaredElement?.Element;
                if (element != null)
                    return element;
            }

            if (lookupItem is IAspectLookupItem<ILookupItemInfo> aspectItem
                && aspectItem.Info is DeclaredElementInfo declaredElementInfo)
            {
                IDeclaredElement element = declaredElementInfo.PreferredDeclaredElement?.Element;
                if (element != null)
                    return element;
            }

            return null;
        }

        private static string CleanTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return typeName;

            int backtick = typeName.IndexOf('`');
            return backtick >= 0 ? typeName.Substring(0, backtick) : typeName;
        }

        private static string ShapeResult(
            string filePath, int line, int column, ICodeCompletionResult completionResult, int maxResults)
        {
            var items = new List<(string text, string kind, string type)>();

            var lookupItems = completionResult?.LookupItems;
            if (lookupItems != null)
            {
                foreach (var evaluated in lookupItems)
                {
                    if (items.Count >= maxResults) break;

                    var lookupItem = evaluated.LookupItem;
                    if (lookupItem == null) continue;

                    string text = null;
                    string type = null;
                    string kind = null;

                    try { text = lookupItem.DisplayName?.Text; } catch { /* presentation may throw */ }
                    try { type = lookupItem.DisplayTypeName?.Text; } catch { /* optional */ }
                    try { kind = GetKind(lookupItem); } catch { /* optional */ }

                    if (string.IsNullOrEmpty(text)) continue;

                    items.Add((text, kind, type));
                }
            }

            var sb = new StringBuilder();
            sb.Append(filePath).Append(':').Append(line).Append(':').Append(column)
              .Append(" - ").Append(items.Count).AppendLine(" completion item(s)");

            if (items.Count == 0)
                sb.AppendLine("note: completion unavailable headless / needs interactive session");

            foreach (var item in items)
            {
                sb.AppendLine();
                sb.Append(item.text);
                if (!string.IsNullOrEmpty(item.kind)) sb.Append(" [").Append(item.kind).Append(']');
                if (!string.IsNullOrEmpty(item.type)) sb.Append(" : ").Append(item.type);
            }

            return sb.ToString().TrimEnd();
        }
    }
}
