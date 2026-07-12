using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XC.VsResharperMcpServer.SmokeTests;

// Minimal JSON-RPC 2.0 client for the plugin's embedded MCP server, mirroring the
// scratch mcp-call.ps1 helper used throughout manual live-testing this project (see
// docs/DEVNOTES.md) - formalized here as a checked-in, reusable test helper rather than a
// one-off script. The server replies with an SSE-framed body ("event: message\ndata: {...}\n\n")
// even for a single-shot POST, not a plain JSON body - ParseResponseBody handles both shapes
// since that framing detail isn't part of this suite's contract and could change.
public sealed class McpTestClient
{
    private static readonly Regex SseDataPattern = new(@"data:\s*(\{.*\})", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private int _nextId;

    public McpTestClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    public async Task<JsonDocument> CallAsync(string method, object? @params = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = @params ?? new { }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(ct);
        return ParseResponseBody(raw);
    }

    private static JsonDocument ParseResponseBody(string raw)
    {
        var match = SseDataPattern.Match(raw);
        return JsonDocument.Parse(match.Success ? match.Groups[1].Value : raw);
    }
}
