using System;
using JetBrains.Util;
using Microsoft.Extensions.Logging;
using JetBrainsLog = JetBrains.Util.ILogger;
using MsLogger = Microsoft.Extensions.Logging.ILogger;
using MsLoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;
using MsLoggerProvider = Microsoft.Extensions.Logging.ILoggerProvider;

namespace XC.VsResharperMcpServer.Host
{
    // Bridges Microsoft.Extensions.Logging (what ModelContextProtocol.Core's McpServer/
    // StreamableHttpServerTransport expect) onto ReSharper's own JetBrains.Util.ILogger, so
    // exceptions the SDK catches internally (e.g. a tool delegate throwing) actually land
    // somewhere visible instead of being silently swallowed by a null logger factory.
    //
    // Both namespaces declare an "ILogger" type, so every reference below is aliased
    // (JetBrainsLog / MsLogger) rather than using the bare name - the bare name is
    // ambiguous (CS0104) as soon as both namespaces are in scope.
    public class JetBrainsLoggerFactory : MsLoggerFactory
    {
        private readonly JetBrainsLog _logger;

        public JetBrainsLoggerFactory(JetBrainsLog logger)
        {
            _logger = logger;
        }

        public MsLogger CreateLogger(string categoryName) => new JetBrainsLoggerAdapter(_logger, categoryName);

        public void AddProvider(MsLoggerProvider provider)
        {
            // No-op: we only ever route to the single JetBrains ILogger.
        }

        public void Dispose()
        {
            // Nothing to dispose - we don't own the underlying JetBrains ILogger's lifetime.
        }

        private class JetBrainsLoggerAdapter : MsLogger
        {
            private readonly JetBrainsLog _logger;
            private readonly string _categoryName;

            public JetBrainsLoggerAdapter(JetBrainsLog logger, string categoryName)
            {
                _logger = logger;
                _categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                var message = "XC.VsResharperMcpServer.MCP[" + _categoryName + "]: " + formatter(state, exception);

                switch (logLevel)
                {
                    case LogLevel.Critical:
                    case LogLevel.Error:
                        if (exception != null) _logger.Error(exception, message);
                        else _logger.Error(message);
                        break;
                    case LogLevel.Warning:
                        _logger.Warn(message);
                        break;
                    default:
                        _logger.Info(message);
                        break;
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();
                public void Dispose() { }
            }
        }
    }
}
