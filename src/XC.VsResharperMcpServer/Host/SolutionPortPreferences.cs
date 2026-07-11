using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XC.VsResharperMcpServer.Host
{
    // Persists "this solution was last served on port X" locally on this machine, so a solution
    // that's opened in a fresh devenv.exe process tends to land back on the same port it had last
    // time - convenient for a static .mcp.json entry that shouldn't have to change every time VS
    // restarts. Deliberately a plain local file store under %LOCALAPPDATA%, not a ReSharper
    // solution-level DotSettings entry: this is a purely local, per-machine preference with no
    // meaning to share across a team (unlike most DotSettings, which are designed to be checked
    // into source control), and a hand-rolled file avoids taking on a new, unvalidated SDK settings
    // API dependency on top of everything else this session already found fragile.
    //
    // One file per solution, named by a hash of its full path (paths contain characters - ':', '\'
    // - that aren't safe raw filenames). Best-effort throughout: a preference read/write failure
    // should never block a solution from opening or the MCP server from starting.
    public static class SolutionPortPreferences
    {
        private static readonly string StoreDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XC.VsResharperMcpServer", "port-preferences");

        public static bool TryGetPreferredPort(string solutionPath, out int port)
        {
            port = -1;
            if (string.IsNullOrEmpty(solutionPath)) return false;

            try
            {
                var file = PathFor(solutionPath);
                if (!File.Exists(file)) return false;

                foreach (var line in File.ReadAllLines(file))
                {
                    if (line.StartsWith("Port: ", StringComparison.Ordinal) &&
                        int.TryParse(line.Substring("Port: ".Length).Trim(), out var parsed))
                    {
                        port = parsed;
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public static void SavePreferredPort(string solutionPath, int port)
        {
            if (string.IsNullOrEmpty(solutionPath) || port <= 0) return;

            try
            {
                Directory.CreateDirectory(StoreDirectory);
                var file = PathFor(solutionPath);
                File.WriteAllText(file,
                    "Solution: " + solutionPath + Environment.NewLine +
                    "Port: " + port + Environment.NewLine +
                    "Updated: " + DateTime.Now.ToString("O"),
                    new UTF8Encoding(true));
            }
            catch
            {
                // Best-effort - losing the preference just means the next process falls back
                // to normal port assignment, not a functional problem.
            }
        }

        private static string PathFor(string solutionPath)
        {
            var normalized = solutionPath.Trim().ToLowerInvariant();
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
                return Path.Combine(StoreDirectory, hex + ".txt");
            }
        }
    }
}
