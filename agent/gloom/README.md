# G-Loom for Claude Code

A Claude Code plugin that connects an agent session to a running Rhino through G-Loom's MCP
endpoint, plus the skills for using it well.

## What it gives an agent

- **The project's memory** — the record of decisions behind a definition, every version, what
  changed between any two of them, the system options, the milestones and the toolchain each was
  pinned on.
- **The live canvas** — what is open, what is failing and why, the data on any output, the
  components installed in *this* Rhino, and a picture of it.
- **A way to change things safely** — a checkpoint before any edit, values set in batches, single
  objects restored, and a commit at the end attributed to the human with the agent named in its
  trailers.

## Setup

1. In Rhino, open the G-Loom panel and set **Agent access** to `Read-write` (or `Read-only` if you
   only want the agent to look). The endpoint is off by default.
2. The panel's **Copy connect command** puts a ready `claude mcp add …` line on the clipboard,
   token included. That is the fastest path and needs nothing else.
3. To use this plugin's `.mcp.json` instead, export the token first:

   ```
   # Windows (PowerShell)
   $env:GLOOM_MCP_TOKEN = Get-Content "$env:APPDATA\G-Loom\mcp\token"

   # macOS / Linux
   export GLOOM_MCP_TOKEN=$(cat ~/Library/Application\ Support/G-Loom/mcp/token)
   ```

   The token lives outside any project on purpose, so it is never committed. Set `GLOOM_MCP_URL`
   too if a second Rhino took the next port (the panel's **Status…** shows the real one).

4. Check it: `/mcp` should list `gloom` as connected.

## Alongside Rhino's own MCP server

G-Loom deliberately does not author graphs — adding, removing and wiring components belongs to
McNeel's `Rhino-MCP-Platform` (a Yak package, MIT). The two are meant to run side by side: that one
edits, this one remembers, reviews and reverts.

To use both, add McNeel's server as a second entry in this `.mcp.json`, following the transport and
port in **their** documentation — it is a separate product with its own installer and its own
defaults, and guessing them here would only go stale. Then work inside a G-Loom edit envelope: the
checkpoint covers whatever changed the canvas, whichever server did it.
