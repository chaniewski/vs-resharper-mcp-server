using System;
using System.Collections.Generic;

namespace XC.VsResharperMcpServer.Core.Tools
{
    // Shared between ApplyQuickFixTool (which refuses these outright) and ListQuickFixesTool (which just
    // flags them in its listing) - see ApplyQuickFixTool's class doc comment and docs/DEVNOTES.md
    // "apply_quick_fix PSI-lock wedge" for the full root-cause writeup. Quick-fix types listed here were
    // confirmed via decompilation to always finish by opening an async, interactive live-template
    // "hotspot" session that never returns headlessly, wedging the shared PSI lock for every other tool
    // until devenv.exe is restarted.
    internal static class HeadlessUnsafeQuickFixes
    {
        public static readonly HashSet<string> BlockedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "JetBrains.ReSharper.Intentions.CSharp.QuickFixes.CreateFromUsageFix",
            "JetBrains.ReSharper.Intentions.CSharp.QuickFixes.RenameLocalWrongRefFix",
            "JetBrains.ReSharper.Intentions.CSharp.QuickFixes.RenameWrongRefFix",
            // Found live 2026-07-12 during verification of the above three: searches nuget.org and
            // presents results for the human to pick from - inherently needs network + interactive
            // selection, hung the same way (confirmed via a real repro, then a restart to recover).
            "JetBrains.ReSharper.Intentions.CSharp.QuickFixes.ImportTypeFromNuGetFix",
        };

        public static bool IsBlocked(string quickFixTypeName) =>
            quickFixTypeName != null && BlockedTypes.Contains(quickFixTypeName);
    }
}
