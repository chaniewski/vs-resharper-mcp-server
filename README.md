# XC.VsResharperMcpServer

A Visual Studio-hosted ReSharper plugin that exposes ReSharper's PSI code intelligence (find usages, go to definition, rename, diagnostics, quick-fixes, refactorings, and more) to Claude Code and other MCP clients over an embedded local HTTP JSON-RPC server.

Ported from [joshua-light/resharper-mcp](https://github.com/joshua-light/resharper-mcp), which does the same for JetBrains Rider.

Status: 1.0.0. 29 tools covering find/navigate/inspect/refactor/write workflows, live-tested against a real Visual Studio + ReSharper instance. See `docs/DEVNOTES.md` for the running build log and design decisions.

## Requirements

- Visual Studio 2026
- ReSharper 2026.1.4 (or compatible - see `docs/DEVNOTES.md` for the pinned SDK/Wave version)

## Building

```powershell
.\scripts\buildPlugin.ps1
```

Builds `src\XC.VsResharperMcpServer` and packs it to `dist\XC.VsResharperMcpServer.<version>.nupkg`.

## Installing

Via ReSharper's own Extension Manager (not Visual Studio's Extensions manager):

1. **ReSharper → Options → Environment → Extension Manager**, click **Add**, point it at the repo's `dist\` folder as a local source (when built locally) or the download folder of the release package (when downloaded pre-built .nuget package).
2. **ReSharper → Extension Manager**, select that source, install `XC.VsResharperMcpServer`, restart Visual Studio.
3. Open any solution. A status bar indicator (`R# MCP: <port>`) confirms the server is running and shows which port it bound to. For scripted verification, check the per-instance marker file instead of grepping logs:
   ```powershell
   Get-ChildItem "$env:TEMP\XC.VsResharperMcpServer\"
   ```

See `docs/DEVNOTES.md` for exact verification steps per milestone and the fast-iteration (`CopyOnBuild`) workflow for after the first install.

**Updating an existing install**: same Extension Manager screen, click **Update** next to the package. The bottom **Update** button stays disabled until you check **"Third-party Plugins Privacy Note"** near the bottom of the dialog - easy to miss since there's no visible cue that it's gating anything.

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

### Getting Claude to actually use these tools

Adding the server to `.mcp.json` makes the tools *available*; it doesn't make an agent *reach for them*. Left alone, Claude defaults to its built-in `Grep`/`Read`/`Edit` for things a symbol-aware tool would do more reliably (renames, usage search) - those built-ins are always in context and habitual, so a semantically-better tool sitting alongside them is easy to ignore.

The one lever that reliably changes this is a project-level `CLAUDE.md` in the repo you're using the tools on, since that file is always loaded into context and Claude follows it consistently. Add a section like this to steer tool choice explicitly:

```markdown
## Using the ReSharper MCP tools

This repo has the `vs-resharper-mcp` server available (Visual Studio + ReSharper must be
open with this solution loaded). Prefer it over generic search/edit for these cases:

- **Renaming a symbol**: use `rename_symbol`, not find-and-replace or a text edit. It
  updates every real reference and skips unrelated text matches.
- **Finding usages or call sites**: use `find_usages` / `get_call_hierarchy`, not `grep`.
  Grep matches text, not symbols - it misses aliases/overloads and catches false positives.
- **Confirming a change compiles**: run `get_diagnostics` before declaring an edit done,
  not just a build.
- **After editing a file with another tool** (or the user edited it manually): call
  `sync_file_from_disk` before further calls against that file, or PSI state goes stale.
- **Changing a method signature**: `change_signature` updates call sites but not
  body-internal usages that only indirectly depend on the changed parameter - review
  those by hand afterward, same caveat as the interactive ReSharper refactoring.
- **Applying a suggested fix**: `apply_quick_fix` is reliable for most fix types, but a
  known set that open an interactive hotspot session are blocked and reported instead of
  risking a hang - if one is blocked, apply it by hand in the IDE.
- **Structural search** (`structural_search`): good for AST-shape patterns
  (`$x$.Foo($y$)`-style), not a `grep` replacement for arbitrary text.
- Position parameters are caret-based (the gap between characters, not a character
  itself) - if a `line`/`column` lookup fails to resolve a symbol you can see is there,
  try the end of the identifier instead of the start.
```

Two smaller things also shape behavior, worth knowing but not worth relying on alone:

- **Tool descriptions** (visible in `tools/list`) help Claude pick *which* tool once it's already decided to use one of this server's tools - each one documents its own known gotchas - but they don't make Claude consider this server in the first place.
- **`ServerInstructions`**, sent once during the MCP `initialize` handshake, is where this project puts cross-cutting guidance that doesn't belong to any single tool (e.g. the position/caret-semantics note above). Whether a given client actually surfaces this to the model varies by implementation, so treat it as a supplement, not the primary mechanism - the `CLAUDE.md` section above is what to rely on.

## License

[MIT](LICENSE)
