using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Util;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace XC.VsResharperMcpServer.Host
{
    // Wraps a single StreamableHttpServerTransport + McpServer behind a plain HttpListener.
    // M1 scope: initialize / notifications-initialized / tools-list (empty) / tools-call all
    // come for free from the SDK once wired up — no hand-rolled JSON-RPC needed (see
    // docs/DEVNOTES.md Step 1 spike). Multi-session/primary-peer support is out of scope until M6.
    public class McpHttpServer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly HttpListener _listener;
        private readonly StreamableHttpServerTransport _transport;
        private readonly McpServer _server;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _serverRunTask;
        private Task _acceptLoopTask;

        public int Port { get; }

        public McpHttpServer(int port, ILogger logger, McpServerOptions options)
        {
            Port = port;
            _logger = logger;

            // Route the MCP SDK's own internal logging (including exceptions it catches from
            // tool delegates and reports back only as a generic "An error occurred invoking 'x'"
            // in the JSON-RPC response) into the ReSharper log, instead of a null logger factory
            // silently swallowing them.
            var loggerFactory = new JetBrainsLoggerFactory(logger);

            _transport = new StreamableHttpServerTransport(loggerFactory);
            _server = McpServer.Create(_transport, options, loggerFactory, null);

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public void Start()
        {
            _listener.Start();
            _serverRunTask = RunServerSafeAsync(_cts.Token);
            _acceptLoopTask = AcceptLoopAsync(_cts.Token);
            _logger.Info("XC.VsResharperMcpServer: HTTP server listening on http://127.0.0.1:" + Port + "/");
        }

        private async Task RunServerSafeAsync(CancellationToken token)
        {
            try
            {
                await _server.RunAsync(token);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "XC.VsResharperMcpServer: McpServer.RunAsync faulted");
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        break;
                    _logger.Error(ex, "XC.VsResharperMcpServer: HTTP accept loop error");
                    continue;
                }

                _ = HandleRequestAsync(context, token);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
        {
            try
            {
                // StreamableHttpServerTransport does not itself set this header (that's normally
                // ASP.NET Core middleware's job) — see docs/DEVNOTES.md Step 1 spike notes.
                if (!string.IsNullOrEmpty(_transport.SessionId))
                    context.Response.Headers["Mcp-Session-Id"] = _transport.SessionId;

                if (context.Request.HttpMethod == "POST")
                {
                    var message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
                        context.Request.InputStream, McpJsonUtilities.DefaultOptions, token);

                    // StreamableHttpPostTransport (SDK-internal, ModelContextProtocol.Core 1.4.1) always
                    // frames a POST response as SSE ("event: message\ndata: {...}") via its own SseEventWriter -
                    // there is no code path in this SDK version that emits a bare JSON body. This must be set
                    // to "text/event-stream" BEFORE the call below, not after: HttpListener flushes response
                    // headers on the first OutputStream write, which happens inside HandlePostRequestAsync
                    // itself. Previously this was hardcoded to "application/json", which every real
                    // spec-compliant MCP client (trusting the Content-Type header, not sniffing the body)
                    // failed to parse - "JSON Parse error: Unexpected identifier 'event'". Invisible to this
                    // project's own manual testing because McpTestClient.cs/mcp-call.ps1 parse SSE-or-JSON
                    // generically regardless of the header - only a real client caught it. See docs/DEVNOTES.md.
                    context.Response.ContentType = "text/event-stream";
                    var wroteResponse = await _transport.HandlePostRequestAsync(message, context.Response.OutputStream, token);
                    if (!wroteResponse)
                    {
                        // Notification-only POST (e.g. notifications/initialized): the SDK contract says to
                        // respond with an empty 202 Accepted in this case, and it writes nothing to the stream
                        // itself here, so headers are still unflushed and safe to override.
                        context.Response.StatusCode = 202;
                    }
                    context.Response.OutputStream.Close();
                }
                else if (context.Request.HttpMethod == "GET")
                {
                    context.Response.ContentType = "text/event-stream";
                    await _transport.HandleGetRequestAsync(context.Response.OutputStream, token);
                }
                else
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "XC.VsResharperMcpServer: HTTP request handling error");
                try
                {
                    // Local single-user dev tool - surfacing the real exception in the body beats a
                    // silent, empty 500 (this is what sent an earlier debugging session down a dead
                    // end guessing at causes with no error text to go on).
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "text/plain";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(ex.ToString());
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    // best-effort
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { _listener.Close(); } catch { /* best-effort */ }
        }
    }
}
