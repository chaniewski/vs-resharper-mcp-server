using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpTransportSpike
{
    internal class Program
    {
        private static async Task Main()
        {
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "spike-server", Version = "0.1.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal)
            };

            options.ToolCollection.Add(McpServerTool.Create(
                (string name) => "pong: " + name,
                new McpServerToolCreateOptions { Name = "ping", Description = "Echoes back a greeting" }));

            var cts = new CancellationTokenSource();
            var transport = new StreamableHttpServerTransport(NullLoggerFactory.Instance);
            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, null);

            var serverRunTask = server.RunAsync(cts.Token);

            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:23741/");
            listener.Start();
            Console.WriteLine("Listening on http://127.0.0.1:23741/ (Ctrl+C to stop)");

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                listener.Stop();
            };

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync();
                    }
                    catch (Exception) when (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    _ = HandleRequestAsync(context, transport, cts.Token);
                }
            }
            finally
            {
                cts.Cancel();
                await serverRunTask.ContinueWith(_ => { });
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context, StreamableHttpServerTransport transport, CancellationToken token)
        {
            try
            {
                if (context.Request.HttpMethod == "POST")
                {
                    var message = await JsonSerializer.DeserializeAsync<JsonRpcMessage>(
                        context.Request.InputStream, McpJsonUtilities.DefaultOptions, token);

                    context.Response.ContentType = "application/json";
                    await transport.HandlePostRequestAsync(message, context.Response.OutputStream, token);
                    context.Response.OutputStream.Close();
                }
                else if (context.Request.HttpMethod == "GET")
                {
                    context.Response.ContentType = "text/event-stream";
                    await transport.HandleGetRequestAsync(context.Response.OutputStream, token);
                }
                else
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Request error: " + ex);
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }
}
