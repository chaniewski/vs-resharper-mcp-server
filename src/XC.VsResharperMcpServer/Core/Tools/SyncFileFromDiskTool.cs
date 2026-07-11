using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // New in 0.2.0. Not from the reference repo - a VS-specific gap the reference never hit,
    // since Rider's own file-system watcher doesn't get suspended the way ReSharper's does inside
    // devenv.exe while the IDE window is inactive (see docs/DEVNOTES.md "stale PSI cache" saga).
    // An external editor writing to a file on disk while VS sits minimized/unfocused leaves PSI's
    // cached view of that file stale for an unbounded time - previously the only fix was regaining
    // window focus or a full restart. This tool forces the resync explicitly and deterministically.
    //
    // Two distinct staleness layers turned out to exist, discovered by testing against the live
    // host (see DEVNOTES): the raw IDocument text is often already fresh on its own (GetText() reads
    // through to disk even while the tracker is suspended) - the actually-stale layer is the PARSED
    // PSI TREE / symbol cache built on top of it, which nothing tells to reparse without an explicit
    // signal. So this tool does both, in order: (1) if the document's text genuinely differs from
    // disk, replace it via a normal write transaction; (2) unconditionally mark the source file dirty
    // and invalidate its PSI files cache via IPsiFiles - the same low-level entry points the
    // platform's own IFileSystemTracker calls when NOT suspended (found by reflecting over
    // JetBrains.ReSharper.Psi.Files.IPsiFiles rather than fighting the tracker's own suspend flag).
    public class SyncFileFromDiskTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;

        public SyncFileFromDiskTool(ISolution solution, IShellLocks shellLocks)
        {
            _solution = solution;
            _shellLocks = shellLocks;
        }

        public string Execute(string filePath = null, string[] filePaths = null)
        {
            // Self-transacting, not ExecuteWrite: this tool deliberately calls MarkAsDirty/
            // InvalidatePsiFilesCache to mark the file as needing a reparse, which is fundamentally
            // incompatible with ExecuteWrite's outer PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate
            // - its auto-commit-on-dispose asserts NO document is left dirty, which a MarkAsDirty call
            // (by definition) always violates. Committing the ReplaceText change explicitly (below) and
            // skipping the outer auto-commit transaction entirely avoids that assertion instead of
            // fighting it. See docs/DEVNOTES.md.
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.sync_file_from_disk", () =>
                ExecuteCore(filePath, filePaths));
        }

        private string ExecuteCore(string filePath, string[] filePaths)
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(filePath)) paths.Add(filePath);
            if (filePaths != null) paths.AddRange(filePaths.Where(p => !string.IsNullOrEmpty(p)));
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (paths.Count == 0)
                return "Provide 'filePath' (single) or 'filePaths' (array) - at least one is required.";

            if (paths.Count == 1)
                return SyncOne(paths[0]);

            var sb = new StringBuilder();
            for (var i = 0; i < paths.Count; i++)
            {
                sb.Append("=== [").Append(i + 1).Append('/').Append(paths.Count).Append("] ").Append(paths[i]).AppendLine(" ===");
                sb.AppendLine(SyncOne(paths[i]));
                if (i < paths.Count - 1) sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private string SyncOne(string filePath)
        {
            var sourceFile = PsiHelpers.GetSourceFile(_solution, filePath);
            if (sourceFile == null)
                return $"File not found in solution: {filePath}";

            var document = sourceFile.Document;
            if (document == null)
                return "Could not get document for file";

            string diskText;
            try
            {
                diskText = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                return $"Could not read file from disk: {ex.Message}";
            }

            var currentText = document.GetText();
            var textChanged = !string.Equals(currentText, diskText, StringComparison.Ordinal);
            if (textChanged)
            {
                document.ReplaceText(new TextRange(0, document.GetTextLength()), diskText);

                // ExecuteWrite's outer PsiTransactionCookie asserts "all documents committed" when it
                // auto-commits on dispose - ReplaceText alone leaves this document dirty, which used to
                // throw JetBrains.Diagnostics.Assertion+AssertionException there instead of returning a
                // result. Committing explicitly here, inside our own action, satisfies that assertion.
                _solution.GetPsiServices().Files.CommitAllDocuments();
            }

            // Unconditional: the parsed PSI tree/symbol cache can be stale even when the document's
            // own text already matched disk (observed directly - see DEVNOTES). MarkAsDirty mirrors
            // what the platform's own file-system tracker does on an unsuspended external change.
            var psiFiles = _solution.GetPsiServices().Files;
            psiFiles.MarkAsDirty(sourceFile);
            psiFiles.InvalidatePsiFilesCache(sourceFile);

            return textChanged
                ? $"{filePath} - text resynced from disk ({currentText.Length} -> {diskText.Length} chars) and PSI cache invalidated."
                : $"{filePath} - text already matched disk; PSI cache invalidated (forced reparse).";
        }
    }
}
