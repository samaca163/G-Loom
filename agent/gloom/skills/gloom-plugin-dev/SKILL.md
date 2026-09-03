---
name: gloom-plugin-dev
description: Building, deploying and testing G-Loom itself - the .gha plugin. Use when working on the G-Loom source rather than on a Grasshopper project.
---

# Working on G-Loom itself

G-Loom is a Grasshopper plug-in (`GLoom.gha`, a renamed .NET assembly) that versions Grasshopper
definitions with git. `CLAUDE.md` at the repo root is the long-form context; `AGENTS.md` is the
short working guide. Read one of them before making changes.

## The loop

```
dotnet build tests/GLoom.Core.Tests/GLoom.Core.Tests.csproj   # fastest, most specific check
dotnet build src/GLoom.Plugin/GLoom.Plugin.csproj -c Release  # zero warnings is the bar
dotnet test  tests/GLoom.Core.Tests/GLoom.Core.Tests.csproj
```

Build the **test project first**. It targets plain `net8.0` and *links* the host-free sources
rather than referencing the plugin, so it is the compiler enforcing that nothing under
`Mcp/Protocol`, `Mcp/State`, `Mcp/Tools/**` or `Vcs/**` has acquired a dependency on Rhino,
Grasshopper or WinForms. If it stops building, something impure was added.

On Windows, set the SDK up first:

```
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"; $env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"
```

## Deploying

```
powershell -ExecutionPolicy Bypass -File build\deploy-local.ps1   # Windows
./build/deploy-local.sh                                          # macOS
```

**Rhino must be closed** — the `.gha` is locked while loaded. Only `GLoom.gha` may sit in the
Grasshopper libraries folder; a stray `GLoom.dll` beside it will be loaded instead and silently
shadow every fix you just made. The deploy script sweeps those, but check if a change "doesn't take
effect".

## The rules that exist because something broke

- **Never invoke a method found by reflection on a Grasshopper SDK object.** Properties and fields
  only. Many no-arg GH methods have side effects, and a blind invoke once killed Grasshopper's
  drawing pipeline for an entire Rhino session.
- **Every git call is a ~50-100 ms process spawn.** Nothing that spawns git, parses JSON or
  serializes a document may run in a paint handler or a hot event path. Go through the existing
  caches.
- **`GH_Document`, `Instances`, the canvas and Eto are UI-thread-only** — macOS aborts the process
  on off-thread access. Everything host-touching goes through `Mcp/Host/UiThread.Run`, which is the
  single bridge. Never call `DocumentTracker.Refresh()` off-thread.
- **Every commit path ends in a forced `DocumentTracker.Refresh()`** plus `NotifyExternalChange()`,
  or the panel and overlay sit stale — a pure git commit fires no document event.
- **No new NuGet packages.** Rhino has one load context, so a second copy of a common library
  breaks the host. This is why the MCP layer is hand-rolled on `HttpListener` and in-box
  `System.Text.Json`, and why LibGit2Sharp was abandoned for shelling out to `git`.
- **Schema changes are additive only.** New fields get `= null` defaults so older documents parse.

## Conventions

Comments explain **why**, never what — default to none, and add one only where removing it would
surprise the next reader. Log lines are prefixed `[G-Loom]` and are for lifecycle events and errors
only. UI strings are neutral and functional; the weaving metaphor is for marketing, not for
buttons. Commit per fix with a conventional prefix, and **push** — the working tree is sometimes
shared between sessions, and unpushed work has been lost to a re-clone before.
