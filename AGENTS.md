# AGENTS.md — working guide for G-Loom

> **▶ FIRST JOB THIS SESSION (set 2026-09-03).** On `experiment/mcp` at `v0.3.0-mcp.4`: rungs 3, 4
> and 5 are built, unit-tested (143 cases, zero warnings) and pushed, and **not one of the 24 MCP
> tools has run inside Rhino**. Smoke-test them before starting anything new. The full case list —
> per tool, including error paths and panel regressions — is the section
> **"THE NEXT SESSION'S FIRST JOB — smoke-test all of it"** in `CLAUDE.md`. Write results into
> `docs/MEMORY.md` as you go. Deploying needs Rhino **closed**; ask the user first.

Start here to begin development. This is the actionable guide: what the
project is, how it's built, how to build/test/deploy, and the rules that keep
it working. For the deeper "why" (thesis, decisions, strategy, roadmap
rationale) read `CLAUDE.md` and `docs/STRATEGY.md`. If they disagree with
this file or the code, the code wins and the docs need fixing.

## What G-Loom is, in one line

**Version control for parametric Grasshopper definitions, built on git.**
It versions the *recipe* — the `.gh` graph that produces geometry, plus a
diff-friendly canonical JSON sidecar — not the geometry itself. Storage stays
tiny, diffs describe *design moves* (wires rerouted, sliders changed,
components added), and history reads as a chain of decisions.

It is a Grasshopper plugin (`GLoom.gha`, a renamed .NET DLL) that loads inside
Rhino/Grasshopper. It is **not** "git for Grasshopper" — position it as
*system versions*, *design options*, *the project's memory*.

## Stack

- **Language / runtime:** C# (LangVersion latest, nullable enabled), `net7.0-windows`.
- **Host:** Rhino 8 + Grasshopper. `GH_Canvas` derives from WinForms, so
  `UseWindowsForms=true` and the target is `-windows`. `EnableWindowsTargeting`
  lets it build on macOS; the resulting `.gha` runs cross-platform inside Rhino
  (Rhino ships its own WinForms shim).
- **UI:** Eto.Forms panel (registered against Grasshopper's plugin id) + a
  GDI+ on-canvas paint overlay. Eto types are **aliased** in every UI file to
  dodge WinForms name collisions (`using Button = Eto.Forms.Button;` etc.).
- **Git:** system `git` CLI, shelled out via `Process`. **No LibGit2Sharp** —
  its native dylib broke type-initialization on macOS and resisted every fix.
- **JSON:** `System.Text.Json` (in-box). **No MCP SDK** in the plugin — the
  experiment/mcp branch hand-rolls the protocol on `HttpListener` + in-box STJ
  so the `.gha` stays a single file and avoids a `System.Text.Json` version
  clash in Rhino's single load context.
- **Packages (compile-only, Rhino provides at runtime):**
  `Grasshopper 8.0.23304.9001` (IncludeAssets=compile;build) and
  `System.Drawing.Common 7.0.0` (IncludeAssets=compile).

## Repo layout (annotated)

```
G-Loom/
├── AGENTS.md                     ← this file (working guide)
├── CLAUDE.md                     ← the deep narrative: thesis, decisions, conventions
├── docs/STRATEGY.md              ← history, industry position, direction, roadmap
├── README.md                     ← public-facing pitch + install + roadmap
├── G-Loom.sln
├── LICENSE                       (MIT)
├── build/
│   ├── deploy-local.ps1          (Win: build + copy .gha → %APPDATA%\Grasshopper\Libraries\G-Loom\)
│   └── deploy-local.sh           (macOS: build + copy .gha → Rhino's GH plug-in Libraries\G-Loom)
└── src/GLoom.Plugin/
    ├── GLoom.Plugin.csproj       (net7.0-windows; AssemblyName=GLoom; TargetExt=.gha)
    ├── GLoomAssemblyInfo.cs      (GH_AssemblyInfo — display metadata; plugin GUID)
    ├── GLoomPriorityLoad.cs      (GH_AssemblyPriority — entry point: init tracker/overlay, register panel, auto-open)
    ├── Vcs/
    │   ├── GLoomRepository.cs    (ALL git ops, shelled out. Records: CommitInfo, BranchInfo, TagInfo, …)
    │   ├── DocumentTracker.cs    (singleton; tracks active GH_Document; reload-from-disk; SuspendUpdates scope)
    │   ├── RepoDiscovery.cs      (walk up to .git dir/gitlink; compute <name>.gloom.json path)
    │   ├── CommitVersioning.cs   (auto-version `<base>_V###`; parse `Gloom-Version:` trailer)
    │   └── ToolchainSnapshot.cs  (Rhino/GH/RiR/G-Loom versions captured at tag time)
    ├── Serialization/
    │   ├── DocumentSerializer.cs (GH_Document → CanonicalDocument; structural + persistent capture, schema v6)
    │   ├── CanonicalModels.cs    (CanonicalDocument/Object/Param/Group/PersistentData records)
    │   ├── CanonicalJson.cs      (JsonSerializerOptions: indented, camelCase, nulls omitted)
    │   ├── DocumentDiff.cs       (Compute(from,to) → added/removed/modified + groups, with Kinds + Summary)
    │   ├── DiffSummaryText.cs    (turns a diff into commit-dialog headline/body draft)
    │   └── TagMetadata.cs        (tag-message JSON: toolchain + notes + AEC/Product/Release)
    └── Ui/
        ├── GLoomPanel.cs         (Eto panel: file/repo/branch/remote/sync/version + history + commit)
        ├── GLoomPanelHost.cs     (Panels.RegisterPanel + dock-as-sibling open + runtime "G" icon)
        ├── CanvasDiffOverlay.cs  (on-canvas paint: halos/ghosts/wire arrows; right-click restore)
        ├── CommitDialog.cs       (modal commit dialog, title + description pre-filled from diff)
        ├── TagCreationDialog.cs  (modal tag dialog with mode-aware sections)
        └── OverlayResources.cs   (pooled GDI+ fonts/pens/brushes — process-lifetime, never disposed)
```

Experiment branches add more (see *Branches* below): `Components/`, `Ui/GLoomIcons.cs`,
`Survey/`, `Model/`, `Mcp/`, `Vcs/CommitTrailers.cs`, `Vcs/GLoomLog.cs`, and `tests/`.

## How it fits together (data flow)

1. Grasshopper loads `GLoom.gha`. `GLoomPriorityLoad.PriorityLoad()` runs:
   inits `DocumentTracker`, `CanvasDiffOverlay`, registers the panel, and on
   first canvas creation auto-opens the panel docked beside another panel.
2. `DocumentTracker` (singleton) listens to `Instances.DocumentServer`
   (add/remove), per-doc `FilePathChanged`/`ModifiedChanged`, and
   `GH_Canvas.DocumentChanged` (tab switches). On each change it computes a
   `TrackedState`: file path, repo root, repo-relative paths, dirty flag, and
   **the SHA whose (.gh, .gloom.json) blob pair matches the working tree**.
3. `GLoomPanel` subscribes to `Tracker.StateChanged`, then on a worker thread
   reads all git facts once (status, branches, remotes, upstream, ahead/behind,
   log, tags, fork points) and marshals only control mutation back to the UI.
4. `GLoomRepository` is the **only** place that spawns git. Every other layer
   goes through it. Its methods are narrow (Commit, Log, GetStatus,
   GetBranches, SwitchBranch, Restore, FindCommitMatchingWorkingTree, …).

**The recipe pair:** each commit stages `<name>.gloom.json` (always) and the
`.gh` (when it changed). "Current version" is re-derived from the filesystem
(blob-pair fingerprint), so it survives Grasshopper restarts with no persisted
marker. Because the serializer is structural, a slider-only edit produces a
byte-identical JSON — that's why the fingerprint hashes *both* files and why
version counting includes both.

**The diff engine** (`DocumentDiff.Compute`) is the single source for both the
panel's per-commit drawer *and* the on-canvas overlay. It returns
`ObjectsAdded/Removed/Modified` (+ group equivalents); each modified change
carries `ObjectChangeKind` flags (Renamed/Moved/WiresChanged/PersistentChanged)
and a human `Summary`. Persistent kinds (slider/panel/boolean/valuelist/color/
gradient/mdslider/data) are compared with noise-suppression (see
`ValueListItem.Canonicalize`, `PersistentData` equality).

**The overlay** (`CanvasDiffOverlay`) paints on `CanvasPostPaintObjects` from a
cached diff. It is event-driven (doc `SolutionEnd`/`ObjectsAdded`/
`ObjectsDeleted`/`UndoStateChanged`, tracker `StateChanged`, reference change),
debounced to 250 ms, with the historic side fetched off the UI thread. The
paint handler is **strictly read-only** against the cache. Right-click restore
is a Win32 `NativeWindow` hook (Windows only; macOS has no `NativeWindow` in
Rhino's shim).

## Commands

**Build** (zero warnings, zero errors is the bar):
```
dotnet build src/GLoom.Plugin/GLoom.Plugin.csproj -c Release
```
Output: `src/GLoom.Plugin/bin/Release/net7.0-windows/GLoom.gha`.

**Tests** (exist on experiment branches, not on `main`):
```
dotnet test tests/GLoom.Core.Tests/GLoom.Core.Tests.csproj        # MCP branch
dotnet test tests/GLoom.Survey.Tests/GLoom.Survey.Tests.csproj    # survey branch
```
Test projects target plain `net8.0` and **link** (not ProjectReference) the
host-free sources, so "purity" (no Rhino/Grasshopper/WinForms) is enforced by
the compiler — if one stops building, something impure was added to a linked
file.

**Deploy locally** (Rhino must be CLOSED — the `.gha` is locked while loaded):
```
powershell -ExecutionPolicy Bypass -File build\deploy-local.ps1    # Windows
./build/deploy-local.sh                                           # macOS
```
Then restart Rhino, open Grasshopper. Command line should print
`[G-Loom] Plugin loaded.` and `[G-Loom] Panel registered (icon: 32x32).`.

**Environment notes (two-machine workflow):**
- Develop on **macOS** (`dotnet` at `~/.dotnet/dotnet`, zsh).
- Smoke-test in Rhino on **Windows** (PowerShell; `git` at
  `C:\Program Files\Git\cmd\git.exe`; .NET SDK 8 at `%USERPROFILE%\.dotnet\` —
  build with `$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"; $env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"` first).
- "Let's smoke test next time" usually means the next session's first job is
  deploying the `.gha` on Windows via `build/deploy-local.ps1`.

## Invariants & pitfalls (non-negotiable)

These are the rules the v0.2.0 performance overhaul and the macOS hardening
exist to protect. Don't regress them.

**Performance** (every git call is a 50–100 ms process spawn; the whole plugin
is built around *not spawning*):
- **Never run git, JSON parsing, or `DocumentSerializer.Serialize` inside a
  paint handler.** `CanvasDiffOverlay.OnPostPaintObjects` only reads
  `_cachedDiff`. Recompute is event-driven, debounced, historic side fetched
  off-thread and cached per `(repo, file, SHA)`. Failed fetches **latch off**
  (`_historicUnavailable`) instead of retrying on a clock.
- **`FindCommitMatchingWorkingTree` is 3 spawns, not O(commits):** one
  multi-file `git hash-object` (not in-process SHA-1 — git applies autocrlf/
  clean filters first, which this repo hits on Windows), one `git log -n200`,
  one `git cat-file --batch-check` in **strict ping-pong** (write a line,
  flush, read a line — batching requests up front deadlocks on the pipe
  buffer). On macOS, NFC-normalize the cat-file request paths.
- **`IsRepo` is memoized per repo root** with a `.git`-marker staleness guard
  (it used to be a hidden spawn on every public method — ~40% multiplier).
- **`DocumentTracker` memoizes the working-tree match** behind a stat key
  (length + LastWriteTimeUtc of both files); `ModifiedChanged` floods never
  touch git. `Refresh()` forces past the memo (checkouts change history without
  changing bytes). `SuspendUpdates()` returns an IDisposable scope that batches
  compound ops (commit / restore / `ReloadAllInRepo`) into exactly one forced
  trailing recompute, and resolves the doc from the **active canvas** (a reload
  replaces `_state.Document` with a dead instance).
- **Paint allocates nothing fixed.** All GDI+ lives in `OverlayResources`
  (process-lifetime). One shared `AdjustableArrowCap` — `Pen.Dispose` does NOT
  dispose a custom cap, so per-frame caps leaked native handles. Live-object and
  wire-anchor maps rebuild per recompute, not per frame; per-frame work is
  O(diff items) with viewport culling (hit-test regions and wire anchors are
  never culled).
- **`RunInternal` drains stdout/stderr concurrently** (two `ReadToEndAsync`;
  sequential `ReadToEnd` deadlocks when git fills the stderr pipe first), with
  timeouts (30 s local, 120 s network) and process-tree kill on expiry.
- If a feature needs "current commit" or repo facts on a hot path, go through
  the existing caches — don't add direct `Run` calls to event handlers.

**Reflection over the GH SDK — properties and fields only, NEVER method
invocation.** Many GH no-arg methods have side effects (Solve / ExpireSolution /
lazy icon init); invoking them during a paint-cycle diff broke GH's drawing
pipeline once. The gradient/MD-slider capture in `DocumentSerializer` reflects
getters it names explicitly; the generic walk reads properties + fields only.

**Cross-platform load safety:**
- macOS Rhino's WinForms shim has **no `NativeWindow`**, so the
  right-click-restore hook and its type must be gated to Windows
  (`OperatingSystem.IsWindows()`), and the field typed `object` so the type
  isn't JIT-loaded at construction on macOS.
- libgdiplus (macOS) silently no-ops some GDI+ calls instead of throwing —
  check rendered bitmaps for **ink**, not just for absence of exception.

**Git output handling:**
- Decode git stdout/stderr as **UTF-8** (the console code page mangles
  non-ASCII). Use `-z` / `core.quotepath=false` so non-ASCII paths aren't
  C-quoted and silently dropped when fed back to git.
- Always forward-slash paths when passing to git (`.Replace('\\','/')`); use
  `Path.GetRelativePath` for repo-relative paths.

**Identity markers (never change):**
- Plugin GUID `fee7144c-f1fc-46d8-8710-100464ca0cb4` (GLoomAssemblyInfo)
- Panel GUID `55f07e53-ad04-44a9-ab21-059f32207842` (GLoomPanel)
  Preserved through the G-BIM → G-Loom rename; installed instances depend on them.

**Schema evolution is additive-only.** New `CanonicalModels` / `TagMetadata`
fields get `= null` defaults; older documents parse cleanly and older plugin
builds ignore additions. Bump `CurrentSchemaVersion` in `DocumentSerializer`.

## Conventions

- **Comments — the WHY, never the WHAT.** Default to none. Add one only when
  removing it would surprise a future reader (hidden constraint, GH/Rhino bug
  workaround, non-local invariant). Don't narrate well-named code. Don't
  reference the current task/PR — that belongs in the commit message.
- **Logging:** every line prefixed `[G-Loom]`; use `Rhino.RhinoApp.WriteLine`
  (not Console/Debug). Sparingly — lifecycle events and errors only. On
  host-free code paths, write through `Vcs/GLoomLog.Sink` (the mcp branch
  points it at RhinoApp at load; tests leave the default).
- **UI strings:** neutral functional labels (`Branch:`, `Commit current
  version`, `History`, `Restore`). The loom/weaving metaphor is for marketing/
  docs, not functional UI.
- **Branches are systems, not detours.** UI copy uses system-vocabulary
  ("Create new system…"), not git-vocabulary, where it reaches the user.
  Internal code may say "branch".
- **Panel-first.** Don't introduce a `GH_Component` subclass on `main` unless
  the value belongs on the wire graph *and* the user asked. (The first to earn
  its place — Project Root — lives on `experiment/canvas-components`.)
- **Scoped branches are opt-in** (post-launch). "Create branch" stays
  git-vanilla; scope is the power feature. Mode 3 (tool dev) users never use it.
- **Cross-platform:** target stays `net7.0-windows`; `System.Drawing.Common`
  stays `IncludeAssets="compile"`. Don't introduce Windows-only APIs without
  checking the macOS shim.
- **Build before committing.** Per-fix commits with conventional prefixes
  (`feat:`, `fix:`, `chore:`, `docs:`, `refactor:`). Commit when asked, not
  defensively.

## Branches

`main` is the launch path and stays shippable. Experiments live on
`experiment/*` and only merge on their own evidence. Versions on `main` move
only for core releases; experiments use SemVer pre-release identifiers naming
the core version they would land as (`v0.2.2`, `v0.2.3`, `v0.3.0-mcp.N`).

| Branch | Holds | Release | Status |
|---|---|---|---|
| `main` | Core: commit/log/restore + dialog, branches, tags + toolchain pinning, canvas diff overlay + right-click restore, remotes/push/pull + smart Sync. **No canvas components, no ribbon tab, no MCP.** | `v0.2.1` | Shipped. Phase 4 + v0.2.1 Windows smoke-test pass still pending. |
| `experiment/canvas-components` | **Project Root** (first `GH_Component`, emits repo root) + branded `G-Loom` ribbon tab, `GLoomComponent` base, drawn icons (`Ui/GLoomIcons.cs`). | `v0.2.2` | Smoke-tested on Windows 2026-08-21. Proven; off main until a deliberate merge decision. |
| `experiment/survey-metadata` | **Survey Schema** + **Classify by Layer** (layer-driven metadata for architectural surveys; `Survey/` pure logic, `Model/ModelObjectBridge`). Branches from canvas-components. `tests/GLoom.Survey.Tests`. | `v0.2.3` (pre-release) | First Rhino run 2026-08-26 (macOS) closed open Q1–3; still provisional. |
| `experiment/mcp` | **MCP endpoint** — G-Loom as the memory/provenance/review layer for coding agents. `Mcp/` (Protocol/State/Host + Host/Live, Tools/Memory + Tools/Live), `Ui/DocumentRestore`, `Vcs/{CommitTrailers,Identity,IndexGate,GLoomLog}`, `agent/gloom/`, `tests/GLoom.Core.Tests`. Branches from canvas-components. | `v0.3.0-mcp.4` (pre-release) | Rungs 1, 1b, 2 passed on Windows 2026-08-26; rungs 3-5 built 2026-09-03 (24 tools, 143 test cases); smoke test pending. |

Grandfathered tags `v0.2.2`/`v0.2.3` predate the branch policy and keep their
names; pre-rewrite state is preserved at tag `backup/pre-branch-rework`.

## Where we are / what's next

**Immediately: smoke-test the MCP surface.** Nothing below starts until that
is done. The per-tool case list lives in `CLAUDE.md`; the shape of it is:

| Area | The cases that matter most |
|---|---|
| Deploy | `[G-Loom] Plugin loaded.`, and `gloom_rhino_context` reporting `0.3.0-mcp.4` — the cheapest proof you are not on a stale `.gha` |
| Connect | `/mcp` lists **24 tools**; any other number means a registration was lost |
| Live read | Move a slider *without saving*: `gloom_read_document` sees it, the recipe on disk does not. Break a component → it appears in `problems`. A screenshot with **ink**, not a blank |
| Errors | Any bad argument names the real problem — "one or more errors occurred" anywhere means `UiThread.Run` regressed |
| Solve | Locked solver and background tab both refused honestly, rather than reporting a stale error list as fresh |
| Envelope | `gloom_begin_edit` commits unsaved work as the checkpoint and **the canvas overlay switches to it**; `gloom_set_value` refused without one; `gloom_end_edit` commits with the four trailers, authored by the human |
| Restore | Value lists actually restore now (they silently did not); a deleted component comes back with its identity and wires |
| Regressions | Right-click restore for every ghost kind — the primitives moved to `Ui/DocumentRestore.cs` this session. Panel commit, branches, remotes, tags unchanged |
| Access | `Off` still *lists* tools but refuses calls; `Read-only` refuses the write ones; foreign `Origin` → 403 |

Then, current course (see `docs/STRATEGY.md` for the full rationale):

- **Phase 5 — De-risk & harden** *(now)*: hands-on Grasshopper 2 verification
  in the Rhino 9 Beta; a format-adapter seam so the canonical schema / diff /
  branch / pinning core is independent of the GH1 file format; GhJSON interop;
  the pending Windows smoke-test pass; test coverage for the pure logic.
- **Phase 6 — Assisted merge** *(the launch-gate feature)*: three-way,
  on-canvas, assisted merge reusing the overlay + right-click-restore
  primitives; non-conflicting changes auto-apply, conflicts resolve
  per-component take-left/take-right. Ship the 80% case; never promise
  auto-merge.
- **Phase 7 — Launch** *(go-public gate)*: Phases 5+6 done, smoke-tested both
  platforms, Yak package + food4rhino listing, demo GIFs, README/site, then the
  repo goes public.
- **Phase 8 — AI layer:** AI commit narratives; MCP-aware review/rollback for
  agent-edited definitions; provenance stamping into Speckle.
- **Phase 9 — Element versioning / Cimbra** (three-lane storage design; the
  extractor is the unbuilt work).
- Demoted to post-launch: scoped branches + promote/refresh.

## Working with the user

- The user is a **designer/architect, not a software engineer**. Workflow-first;
  synthesizes big ideas; when they ask "what do you think?" they want a real
  opinion with tradeoffs, not a survey of options.
- For exploratory questions: 2–3 sentences with a recommendation + the main
  tradeoff. Go deep only when they ask to discuss a topic in depth.
- Build before committing (zero warnings). Deploy needs Rhino closed — the user
  says "go" once it's closed; don't proactively redeploy. Commit when asked.
- Prefer per-fix commits with conventional prefixes.
