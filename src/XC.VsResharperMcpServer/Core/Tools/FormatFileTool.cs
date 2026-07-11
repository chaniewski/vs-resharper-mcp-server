using JetBrains.Application.Threading;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.CodeCleanup;
using JetBrains.ReSharper.Features.Altering.CodeCleanup;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CodeStyle;
using JetBrains.ReSharper.Psi.Transactions;
using JetBrains.Util;
using XC.VsResharperMcpServer.Core.Psi;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Ported from resharper-mcp's FormatFileTool (see docs/DEVNOTES.md). Batch mode dropped for M4.
    // Self-transacting: cleanup/style modes use CodeCleanupRunner which manages its own PSI
    // transactions; format mode opens its own transaction inline (matching the reference).
    public class FormatFileTool
    {
        private readonly ISolution _solution;
        private readonly IShellLocks _shellLocks;
        private readonly CodeCleanupSettingsComponent _cleanupSettings;

        public FormatFileTool(ISolution solution, IShellLocks shellLocks, CodeCleanupSettingsComponent cleanupSettings)
        {
            _solution = solution;
            _shellLocks = shellLocks;
            _cleanupSettings = cleanupSettings;
        }

        public string Execute(string filePath, string mode = "format")
        {
            return PsiThreadDispatcher.ExecuteSelfTransactingWrite(_shellLocks, _solution, "XC.VsResharperMcpServer.format_file", () =>
                ExecuteCore(filePath, mode));
        }

        private string ExecuteCore(string filePath, string mode)
        {
            if (string.IsNullOrEmpty(filePath))
                return "filePath is required";

            var resolved = PsiHelpers.ResolveFile(_solution, filePath);
            if (!resolved.IsFound)
                return resolved.Error;
            var sourceFile = resolved.SourceFile;

            var psiFile = PsiHelpers.GetPsiFile(sourceFile);
            if (psiFile == null)
                return "Could not get PSI tree for file";

            if (mode == "format")
            {
                var language = psiFile.Language;
                var formatter = language.LanguageService()?.CodeFormatter;
                if (formatter == null)
                    return $"No code formatter available for language: {language.Name}";

                using (PsiTransactionCookie.CreateAutoCommitCookieWithCachesUpdate(
                    _solution.GetPsiServices(), "XC.VsResharperMcpServer.format_file"))
                {
                    formatter.FormatFile(psiFile, CodeFormatProfile.DEFAULT, null);
                }

                return $"{filePath} - formatted successfully";
            }

            CodeCleanupService.DefaultProfileType profileType;
            switch (mode)
            {
                case "cleanup":
                    profileType = CodeCleanupService.DefaultProfileType.FULL;
                    break;
                case "style":
                    profileType = CodeCleanupService.DefaultProfileType.CODE_STYLE;
                    break;
                default:
                    return $"Unknown mode: {mode}. Use 'format', 'cleanup', or 'style'.";
            }

            var profile = _cleanupSettings.GetDefaultProfile(profileType);
            if (profile == null)
                return $"No default cleanup profile found for mode: {mode}";

            var filesProvider = new SingleFileCleanupProvider(_solution, resolved.ProjectFile, sourceFile);
            CodeCleanupRunner.CleanupFiles(filesProvider, profile, true);

            return $"{filePath} - {mode} completed successfully";
        }

        private class SingleFileCleanupProvider : ICodeCleanupFilesProvider
        {
            private readonly ISolution _solution;
            private readonly IProjectFile _projectFile;
            private readonly IPsiSourceFile _sourceFile;

            public SingleFileCleanupProvider(ISolution solution, IProjectFile projectFile, IPsiSourceFile sourceFile)
            {
                _solution = solution;
                _projectFile = projectFile;
                _sourceFile = sourceFile;
            }

            public ISolution Solution => _solution;
            public IProjectItem ProjectItem => _projectFile;

            public System.Collections.Generic.IReadOnlyList<IPsiSourceFile> GetFiles()
            {
                return new[] { _sourceFile };
            }

            public DocumentRange[] GetRangesForFile(IPsiSourceFile file)
            {
                var document = file.Document;
                if (document == null)
                    return null;

                var range = new DocumentRange(document, new TextRange(0, document.GetTextLength()));
                return new[] { range };
            }

            public bool IsSuitableProjectElement(IProjectModelElement element)
            {
                return true;
            }

            public bool IsSuitableFile(IProjectFile file)
            {
                return file.Location.FullPath == _projectFile.Location.FullPath;
            }
        }
    }
}
