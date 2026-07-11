# XC.VsResharperMcpServer

A Visual Studio-hosted ReSharper plugin that exposes ReSharper's PSI code intelligence (find usages, go to definition, rename, diagnostics, quick-fixes, refactorings, and more) to Claude Code and other MCP clients over an embedded local HTTP JSON-RPC server.

Ported from [joshua-light/resharper-mcp](https://github.com/joshua-light/resharper-mcp), which does the same for JetBrains Rider.

Status: in development. See `docs/DEVNOTES.md` for the running build log and design decisions.

## Requirements

- Visual Studio 2026
- ReSharper 2026.1.4 (or compatible - see `docs/DEVNOTES.md` for the pinned SDK/Wave version)

## Building

```powershell
.\scripts\buildPlugin.ps1
```

Builds `src\XC.VsResharperMcpServer` and packs it to `dist\XC.VsResharperMcpServer.<version>.nupkg`.

## Installing (local dev)

Via ReSharper's own Extension Manager (not Visual Studio's Extensions manager):

1. **ReSharper → Options → Environment → Extension Manager**, click **Add**, point it at the repo's `dist\` folder as a local source.
2. **ReSharper → Extension Manager**, select that source, install `XC.VsResharperMcpServer`, restart Visual Studio.
3. Open any solution. A status bar indicator (`R# MCP: <port>`) confirms the server is running and shows which port it bound to. For scripted verification, check the per-instance marker file instead of grepping logs:
   ```powershell
   Get-ChildItem "$env:TEMP\XC.VsResharperMcpServer\"
   ```

See `docs/DEVNOTES.md` for exact verification steps per milestone and the fast-iteration (`CopyOnBuild`) workflow for after the first install.

## Using with Claude Code

Once the plugin is running (starts automatically when a solution is opened in Visual Studio), add to your MCP client config:

```json
{
  "mcpServers": {
    "vs-resharper-mcp": {
      "type": "http",
      "url": "http://127.0.0.1:23741/"
    }
  }
}
```

Override the port with the `XC_VSRESHARPERMCPSERVER_PORT` environment variable.

### Multiple solutions open at once

Each Visual Studio instance runs its own independent server. The first one to start binds port `23741`; each subsequent instance falls back to the next free port (`23742`, `23743`, ...). A solution remembers the port it was last assigned (stored locally under `%LOCALAPPDATA%\XC.VsResharperMcpServer\`) and prefers that port again next time it's opened fresh, so a given `.mcp.json` entry tends to keep pointing at the right instance across restarts - but if you're running several solutions side by side, add one server entry per port and use the `list_solutions` tool (callable on any already-configured connection) to see which port currently serves which solution.

## License

[MIT](LICENSE)
