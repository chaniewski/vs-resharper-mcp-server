using System.Text.RegularExpressions;

namespace XC.VsResharperMcpServer.SmokeTests;

// Finds a currently-bound XC.VsResharperMcpServer instance to test against, by reading the same
// marker-file registry the plugin itself writes (McpShellComponent.MarkerDirectory /
// "instance-port-{port}.txt" - see docs/DEVNOTES.md). This suite deliberately does NOT launch its
// own devenv.exe instance: the plugin only runs inside a real, already-loaded ReSharper/VS host, so
// "build a smoke test suite" here means "test the instance a developer/CI already has running with a
// solution open" rather than standing up a fresh one - see the "smoke test suite" DEVNOTES section for
// the reasoning. When no instance is found (or the one found doesn't actually respond), tests using
// this locator report Skipped rather than Failed - a machine with no devenv running isn't a real
// regression signal for this suite.
public static class McpServerLocator
{
    public const int PortRangeStart = 23741;
    public const int PortRangeLength = 10;

    private static readonly string MarkerDirectory =
        Path.Combine(Path.GetTempPath(), "XC.VsResharperMcpServer");

    private static readonly Regex PortFilePattern = new(@"^instance-port-(\d+)\.txt$", RegexOptions.Compiled);

    public static async Task<string?> FindLiveBaseUrlAsync(HttpClient http, CancellationToken ct = default)
    {
        if (!Directory.Exists(MarkerDirectory))
            return null;

        foreach (var file in Directory.GetFiles(MarkerDirectory, "instance-port-*.txt"))
        {
            var match = PortFilePattern.Match(Path.GetFileName(file));
            if (!match.Success) continue;

            var baseUrl = $"http://127.0.0.1:{match.Groups[1].Value}/";
            if (await IsAliveAsync(http, baseUrl, ct))
                return baseUrl;
        }

        return null;
    }

    private static async Task<bool> IsAliveAsync(HttpClient http, string baseUrl, CancellationToken ct)
    {
        try
        {
            var client = new McpTestClient(http, baseUrl);
            using var doc = await client.CallAsync("tools/list", ct: ct);
            return doc.RootElement.TryGetProperty("result", out _);
        }
        catch
        {
            return false;
        }
    }
}
