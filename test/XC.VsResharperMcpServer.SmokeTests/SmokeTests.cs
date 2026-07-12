using Xunit;

namespace XC.VsResharperMcpServer.SmokeTests;

// Checked-in equivalent of the manual mcp-call.ps1-driven live testing done throughout this
// project's development (see docs/DEVNOTES.md). Targets whatever XC.VsResharperMcpServer instance
// is already bound and reachable via McpServerLocator - it does not launch devenv.exe itself (see
// McpServerLocator's doc comment for why). Each test independently locates the server and reports
// Skipped (via Xunit.SkippableFact's Skip.IfNot) rather than Failed when none is found, so running
// `dotnet test` on a machine with no devenv open is inconclusive, not a red build.
//
// The four assertions mirror the original project plan's testing strategy: (1) server binds within
// the documented port range, (2) initialize handshake is well-formed, (3) tools/list returns the
// expected tool roster, (4) one read tool executes and returns a well-formed result. Assertion 4
// deliberately uses list_solutions rather than a PSI-backed tool (e.g. get_solution_structure):
// list_solutions only reads the marker-file registry and is documented to work even before any
// solution is open (see ListSolutionsTool's doc comment), so it's the one tool whose correct output
// doesn't depend on which solution happens to be open in the instance under test - everything else
// would require a checked-in fixture solution actually open in the target devenv, which this suite
// doesn't control.
public class SmokeTests
{
    private static readonly HttpClient Http = new();

    // The exact 29-tool roster live-confirmed via tools/list against a real running instance
    // (see docs/DEVNOTES.md "structural_search hang - root cause and fix (0.8.1)"). Asserted as a
    // subset rather than an exact-match set: a newly *added* tool shouldn't fail this suite, only a
    // missing one should - that's a stronger regression signal for "did something break" than a
    // brittle exact-count check that fails on every legitimate new tool.
    private static readonly string[] ExpectedTools =
    {
        "apply_quick_fix", "apply_suggestions", "browse_namespace", "change_signature",
        "code_metrics", "extract_method", "find_implementations", "find_usages", "fix_usings",
        "flow", "format_file", "generate_members", "generate_xml_doc", "get_call_hierarchy",
        "get_diagnostics", "get_solution_structure", "get_symbol_info", "get_symbol_source",
        "get_type_hierarchy", "go_to_definition", "inline_variable", "list_quick_fixes",
        "list_solutions", "list_symbols_in_file", "move_type", "rename_symbol", "search_symbol",
        "structural_search", "sync_file_from_disk"
    };

    private static async Task<string?> LocateServerAsync() =>
        await McpServerLocator.FindLiveBaseUrlAsync(Http);

    [SkippableFact]
    public async Task Server_Binds_Within_Documented_Port_Range()
    {
        var baseUrl = await LocateServerAsync();
        Skip.IfNot(baseUrl != null, NoServerSkipReason);

        var port = new Uri(baseUrl!).Port;
        Assert.InRange(port, McpServerLocator.PortRangeStart,
            McpServerLocator.PortRangeStart + McpServerLocator.PortRangeLength - 1);
    }

    [SkippableFact]
    public async Task Initialize_Handshake_Returns_Valid_Response()
    {
        var baseUrl = await LocateServerAsync();
        Skip.IfNot(baseUrl != null, NoServerSkipReason);

        var client = new McpTestClient(Http, baseUrl!);
        using var doc = await client.CallAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "XC.VsResharperMcpServer.SmokeTests", version = "1.0.0" }
        });

        var root = doc.RootElement;
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.False(root.TryGetProperty("error", out _), "initialize returned a JSON-RPC error");

        var result = root.GetProperty("result");
        Assert.True(result.TryGetProperty("protocolVersion", out _), "initialize result missing protocolVersion");
        Assert.True(result.TryGetProperty("capabilities", out _), "initialize result missing capabilities");
    }

    [SkippableFact]
    public async Task ToolsList_Returns_Expected_Roster()
    {
        var baseUrl = await LocateServerAsync();
        Skip.IfNot(baseUrl != null, NoServerSkipReason);

        var client = new McpTestClient(Http, baseUrl!);
        using var doc = await client.CallAsync("tools/list");

        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .Where(n => n != null)
            .ToHashSet();

        var missing = ExpectedTools.Where(expected => !names.Contains(expected)).ToList();
        Assert.True(missing.Count == 0,
            $"Missing expected tool(s): {string.Join(", ", missing)}. Actual roster ({names.Count}): {string.Join(", ", names.OrderBy(n => n))}");
    }

    [SkippableFact]
    public async Task ReadTool_Executes_And_Returns_WellFormed_Result()
    {
        var baseUrl = await LocateServerAsync();
        Skip.IfNot(baseUrl != null, NoServerSkipReason);

        var client = new McpTestClient(Http, baseUrl!);
        using var doc = await client.CallAsync("tools/call", new
        {
            name = "list_solutions",
            arguments = new { }
        });

        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out _), "list_solutions returned a JSON-RPC error");

        var content = root.GetProperty("result").GetProperty("content");
        Assert.True(content.GetArrayLength() > 0, "list_solutions returned no content items");

        var text = content[0].GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text), "list_solutions returned empty text");
        // The instance answering this call is by definition alive, so the registry it reads from
        // must report at least one instance - a real content assertion, not just "didn't crash".
        Assert.Contains("instance(s) found", text);
    }

    private const string NoServerSkipReason =
        "No running XC.VsResharperMcpServer instance found (marker directory empty or all instances " +
        "unresponsive) - start Visual Studio with the plugin installed and a solution open to run this " +
        "suite live.";
}
