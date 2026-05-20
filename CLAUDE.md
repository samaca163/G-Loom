# CLAUDE.md — context for future Claude Code sessions on G-Loom

This file is the single document a future Claude session should read first to get oriented. It captures the project's thesis, architecture, conventions, and the design decisions that aren't obvious from the code. The user's persistent memory under `~/.claude/projects/D--Code-Projects-G-BIM/memory/` holds the same conclusions in smaller pieces — this file is the consolidated narrative.

> Note on the directory name: the repo lives in `D:\Code Projects\G-BIM\` for legacy reasons (the project was previously named G-BIM). The product is **G-Loom**; the local folder name is incidental and can be renamed at the user's convenience without touching code.

## What G-Loom is, in one sentence

**Version control for parametric Grasshopper systems** — git-style, but versions the *recipe* (the Grasshopper graph that produces the geometry) rather than the geometry itself.

## The thesis (read this first)

Existing AEC tools version the *result*: a 500–800 MB Revit central file, snapshot per save, hundreds of GB over a project lifetime. Diffs are meaningless ("all bytes changed"). History is a flipbook of frames.

G-Loom flips it: version the parametric recipe. The recipe is single-digit MB. Diffs describe *design moves* — wires rerouted, components added, slider values changed. History becomes a chain of reasoned decisions. Geometry is regenerated from the recipe on demand, or pulled from an LFS-cached snapshot at named milestones.

This is not "git for Grasshopper". It's **process-versioning for parametric design**, and it's what makes the whole substrate (branches, commits, tags, push/pull, merge) feel native to AEC and product workflows instead of grafted on.

## Three supported workflow modes

The same substrate serves three distinct user types. Don't optimize for one and break the others.

### Mode 1 — AEC parametric design (primary)
Buildings, master plans, infrastructure. Branches are *system options* (substitutable design strategies — `envelope-mullion`, `structural-concrete-frame`, `mep-vrf-system`). Compliance audit horizon is decades. Multi-discipline coordination matters. Branches live for the project's duration; some are archived as built versions.

### Mode 2 — Product Design (secondary, fully supported)
Industrial design, furniture, consumer products. Branches are *product variants* (`aria-pro-medium-mesh-charcoal`, `aria-pro-large-leather-tan-EU`) — long-lived, often indefinite (a product stays on the market for years). Sub-mechanisms iterate as scoped branches. Manufacturing handoff at tagged releases. Cross-product reuse (a mechanism shared between Aria Pro and Aria Lite) is common.

### Mode 3 — Tool/Library development (tertiary, fully supported)
Analytical Grasshopper definitions: GIS-to-3D city builders, sunlight analyzers, real-estate prognosticators. Branches are *features and bugfixes* (software-style). Tool-version pinning at every tag is **the** feature — these tools must still run on Rhino 10 / GH 5 / Revit 2032 in 2032. Push/pull workflows for collaborative tool development. No scope/promote needed (the whole tool is small enough to recompute fully).

A single user can occupy all three modes across different repos. The panel UX should flex by branch "kind" metadata, not assume one mode.

## Core concepts (the vocabulary we use)

### Recipe versioning
The unit-of-work G-Loom versions is the **recipe**: the Grasshopper graph + persistent values, captured as a canonical JSON in `<name>.gloom.json` alongside each `.gh`. The `.gh` itself is also committed (it's authoritative; the JSON is diff-friendly). Geometry is *not* versioned directly — it's a side effect.

### System branch
A branch represents a substitutable design strategy ("envelope-mullion" vs "envelope-unitized"), not a "place where work happened". Branches are catalog entries, not detours. Naming conventions follow `<system-kind>-<short-description>`. We plan to record an explicit `kind` field on each branch in `.gloom/branches.json` (or per-branch `.gloom/scope.json`) to drive UI grouping.

### Scoped branch (Phase 6+)
A scoped branch is a sub-region of the parametric definition with the upstream input *internalized* at a chosen cut point. Designer picks a cut (an output port on the canvas), G-Loom captures the data flowing through it, creates a new `.gh` containing only the downstream subgraph + an internalized input param, commits on the scoped branch. Switching to that branch is fast — only the small subgraph re-solves.

Scoped branches make recipe-versioning performance-feasible for large definitions: iterate on a facade without recomputing the whole tower.

### Promote / Refresh (Phase 6+)
The two arrows that round-trip the scoped-branch model:

- **Promote**: scoped branch → `main`. Splice the scoped subgraph back into the trunk, restoring the trunk's upstream chain. New commit on `main` captures "the project with this system version integrated".
- **Refresh**: `main` → scoped branch's input. Re-snapshot the cut from the latest `main`, replace the stale internalized blob, new commit on the scoped branch.

Both are explicit user actions. No magic auto-sync.

### Toolchain pinning
Every tag records the Rhino / GH / RiR / plugin versions used to produce that recipe. Without this, recipe-versioning fails at decade horizons (Rhino 10 / GH 5 / RiR 4 in 2032 may not run a 2026 recipe identically). Combined with LFS-cached post-solve geometry at the same tag, the geometry is recoverable even if the runtime has rotted.

### Recipe vs deliverable
The recipe is the durable artifact. Deliverables (`.rvt`, `.ifc`, `.dwg`, `.step`, `.iges`) are exports generated *from* the recipe at tag time. AEC contracts demand deliverable files; G-Loom's job is to make those exports reproducible from a versioned recipe.

## Architecture (current)

### Project layout

```
G-Loom/
├── G-Loom.sln
├── README.md
├── CLAUDE.md                     ← this file
├── LICENSE                       (MIT)
├── build/
│   ├── deploy-local.ps1          (Windows: builds + copies .gha to %APPDATA%\Grasshopper\Libraries\G-Loom\)
│   └── deploy-local.sh           (macOS: builds + copies .gha to Rhino's GH plug-in folder)
└── src/
    └── GLoom.Plugin/
        ├── GLoom.Plugin.csproj   (net7.0-windows; AssemblyName=GLoom; output: GLoom.gha)
        ├── GLoomAssemblyInfo.cs  (GH_AssemblyInfo subclass — display metadata for the plugin)
        ├── GLoomPriorityLoad.cs  (GH_AssemblyPriority — entry point, hooks canvas + auto-opens panel)
        ├── Serialization/
        │   ├── CanonicalJson.cs       (JsonSerializerOptions — indented, camelCase, deterministic)
        │   ├── CanonicalModels.cs     (CanonicalDocument / Object / Param / Group records, schema v1)
        │   └── DocumentSerializer.cs  (walks GH_Document → CanonicalDocument; structural-only Phase 1a)
        ├── Ui/
        │   ├── GLoomPanel.cs          (Eto.Forms panel — file/repo/branch/version metadata + commit + history list)
        │   └── GLoomPanelHost.cs      (Panels.RegisterPanel + dock-as-sibling open + runtime-rendered "G" icon)
        └── Vcs/
            ├── CommitVersioning.cs    (auto-versioned message format `<base>_V###`)
            ├── DocumentTracker.cs     (singleton tracking active GH_Document; reload-from-disk; multi-doc reload)
            ├── GLoomRepository.cs     (all git ops via shelling out to system `git`)
            └── RepoDiscovery.cs       (walks up to find .git; computes sibling .gloom.json path)
```

### How the layers fit together

1. **Grasshopper loads `GLoom.gha`** (a renamed .NET DLL).
2. **`GLoomPriorityLoad.PriorityLoad`** runs: initializes `DocumentTracker`, registers the panel via `GLoomPanelHost.Register`, hooks `Instances.CanvasCreated`, and on first canvas creation auto-opens the panel docked next to an existing panel.
3. **`DocumentTracker`** (singleton) listens to `Instances.DocumentServer` (add/remove), per-doc events (FilePathChanged / ModifiedChanged), and `GH_Canvas.DocumentChanged` (tab switches). It computes a `TrackedState` whenever the active document changes — file path, repo root, repo-relative paths, dirty flag, and the SHA of the commit whose `.gh + .gloom.json` blob pair matches the working tree.
4. **`GLoomPanel`** subscribes to the tracker's `StateChanged` event. On each refresh it queries `GLoomRepository` for branches, last-commit, history, and renders Eto controls (a button-as-dropdown for branches, a vertical stack for history rows with Restore buttons).
5. **`GLoomRepository`** is the only place that shells out to `git`. Every other layer goes through it. Methods are intentionally narrow (Commit, Log, GetStatus, GetBranches, CreateBranch, SwitchBranch, DeleteBranch, Restore, FindCommitMatchingWorkingTree, ListAffectedGhFiles, CountCommitsTouching).

### Why we use system `git`, not LibGit2Sharp

Tried LibGit2Sharp first; on Rhino 8 macOS its native dylib's type initializer failed in ways that resisted custom DllImportResolver, NativeLibrary preloading, GlobalSettings.NativeLibraryPath, and install-name symlinks. Shelling out to the system `git` CLI is ~50–100ms per call but rock solid. See `GLoomRepository.cs:GitBinary()` for the per-OS binary discovery; falls back to PATH if the canonical location isn't found.

## Conventions (how we write code in this project)

### Comments — the WHY, never the WHAT
Default to no comments. Add one only when removing it would surprise a future reader: a hidden constraint, a workaround for a specific GH/Rhino bug, an invariant that's not local to the code. Don't narrate what well-named code already says. Don't reference the current task or PR ("added for the X flow") — that belongs in the commit message.

### Logging
Every G-Loom log line is prefixed `[G-Loom]`. Use `Rhino.RhinoApp.WriteLine` (not Console.WriteLine, not Debug). Be sparing — write only on lifecycle events (load, register, commit, restore, branch ops, errors). The user reads these in Rhino's command line; don't flood it.

### UI strings
Use neutral functional labels in the Eto panel (`Branch:`, `Commit current version`, `History`, `Restore`). Save the loom/weaving metaphor for marketing/docs, not UI labels — pure metaphor in functional UI gets in users' way. The user has confirmed this preference.

### Cross-platform
Project targets `net7.0-windows` (because GH_Canvas derives from System.Windows.Forms.Control). `EnableWindowsTargeting=true` lets it build on macOS; the resulting `.gha` runs cross-platform inside Rhino, which ships its own WinForms shim. Don't introduce Windows-only APIs without checking. `System.Drawing.Common` is `IncludeAssets="compile"` only — Rhino provides it at runtime.

### Path handling
- Always forward-slashed when passing to `git` (`.Replace('\\', '/')`).
- Use `Path.GetRelativePath` to compute repo-relative paths.
- Verify existence before reading; many ops assume a tracked .gh exists in the working tree.

### GUIDs are identity
Don't change the plugin GUID (`fee7144c-f1fc-46d8-8710-100464ca0cb4`) or the panel GUID (`55f07e53-ad04-44a9-ab21-059f32207842`) — they're identity markers that installed instances depend on. Even during the G-BIM → G-Loom rename, GUIDs were preserved.

### Branches are systems, not detours
When designing UI flows or vocabulary around branches, use system-vocabulary ("Create new system…" / "System options for this slot…") rather than git-vocabulary in user-facing copy. Internal code can use "branch"; UI nudges users toward "system".

### Scoped branches are opt-in
Don't make scope-mandatory. "Create branch" is git-vanilla; "Create scoped branch from cut" is the power feature. Mode 3 (tool development) users will never use scope. Don't force them through it.

### Don't add ribbon components
G-Loom is panel-only. Don't introduce `GH_Component` subclasses unless the feature genuinely belongs on the canvas wire graph and the user has explicitly asked for it. Phase 0 had three diagnostic components; they're all purged. Adding components reintroduces the G-Loom ribbon tab the user explicitly asked us to remove.

## Working with the user

### Communication style
The user is a designer / architect, not a software engineer. They think workflow-first. They synthesize big ideas; they're not asking for shallow implementations. When they ask "what do you think?", they want a real opinion with tradeoffs, not a survey of options.

For exploratory questions, respond in 2–3 sentences with a recommendation and the main tradeoff. Save longer analysis for when they explicitly ask to discuss a topic in depth (then go deep).

### Platform
The user runs a **two-machine workflow**: develops on **macOS** (`/Users/isamaca/Tech Projects/G-Loom/`, `dotnet` at `$HOME/.dotnet/dotnet`, zsh) and tests in Rhino on **Windows** (PowerShell, `dotnet` at `C:\Program Files\dotnet\`, `git` at `C:\Program Files\Git\cmd\git.exe`). The `.gha` is cross-platform compiled, but Phase smoke-testing always happens on Windows where their production Rhino lives — so a session that ends with "let's smoke test next time" usually means the next session's first job is **deploying the .gha on Windows** via `build/deploy-local.ps1`. See `memory/user_platform.md` for paths and tooling on both sides.

Lockstep both deploy scripts when you change deploy logic — the user may test one side at a time but expects both to stay in sync.

### Workflow expectations
- **Build before committing.** `dotnet build src/GLoom.Plugin/GLoom.Plugin.csproj -c Release` should succeed cleanly. Zero warnings, zero errors.
- **Deploy needs Rhino closed.** The `.gha` is locked while Rhino has it loaded. The user typically asks "go" once they've closed Rhino; don't proactively redeploy.
- **Commit when asked, not before.** The user may iterate on testing for a while between commits. Don't commit defensively.
- **Per-fix commits.** When multiple distinct bugs are fixed in one session, prefer multiple focused commits with conventional prefixes (`feat:`, `fix:`, `chore:`, `docs:`, `refactor:`). The user has explicitly preferred this and we've split commits via reset-and-replay before.

### What the user has reviewed and approved
- Recipe-versioning thesis (the central pitch)
- Three-mode scope (AEC primary, Product secondary, Tool/Library tertiary)
- Branches-as-systems framing
- Scoped branches + promote/refresh round-trip (Phase 6 design — not yet built)
- Phase order: tags → visual diff → remotes → merge → scoped branches → heavy-geom → polish
- Panel-only UX (no GH ribbon components)
- Name: G-Loom (renamed from G-BIM)

## What's done and what's next

### Phase 1 — Done (through `2e56f85`)

- Loadable `.gha` (cross-platform, Windows + macOS Rhino 8)
- Canonical structural JSON serializer (`<name>.gloom.json` alongside the `.gh`)
- Eto panel docked + auto-active, with white "G" tab icon
- Auto-versioned commits (`<basename>_V###`) via system `git`
- Restore (file-only `git checkout <sha> -- <files>`, doesn't move HEAD)
- Multi-file repo isolation (each `.gh` sees only its own commits in the panel)
- Tab-switch sync (panel tracks active document)
- Branch ops: list / create / switch / delete with system-aware UI
- Reload-all-in-repo on branch switch (originally-active doc stays active)
- Paired `.gh + .gloom.json` blob fingerprint for "current commit" arrow
- Cross-platform deploy scripts

### Phase 2 — Done (through `d73ace4`)

- **Branch rename** via panel dropdown (`f1f8a44`)
- **Branch-base markers** in history: small `↰ branched from <names>` badge above the merge-base commit, anchored to the default branch (origin/HEAD or local main/master) and the closest other branch by merge-base timestamp; deduplicated when both resolve to the same SHA (`3b73f8e`)
- **Per-commit details drawer** with `▾ / ▴` toggle and expansion state preserved across panel refresh; lives below each commit row and is the home for tag listing today and toolchain/per-tag schema metadata going forward (`8955a60`)
- **Tags** with create/delete (`+ add tag`, `[×]`) inside the drawer (`8955a60`)
- **Toolchain metadata at tag time**: Rhino + Grasshopper + RhinoInsideRevit (if loaded) + G-Loom versions captured automatically and embedded as JSON in the annotated tag message — no working-tree files, no extra commits, travels intact with `git push --tags` (`b053842`)
- **Mode-aware per-tag schema** (v2): always-available `notes` plus optional `aec` (phase / submittal / sheet set / notes), `product` (sku / variant / notes), and `release` (version / notes) sub-records. Tag creation uses a custom Eto dialog with collapsible sections; only expanded sections contribute to the tag. v1 messages still parse cleanly because new params have `= null` defaults (`d73ace4`)
- **Tag-name auto-rewrite** (space → hyphen) in the dialog so the user never hits git's check-ref-format wall (`d73ace4`)

### Phase 3 — Done (through `e74e811`)

**Phase 1b** (`620e0cb`) — Canonical JSON serializer extended with persistent values: typed handlers for sliders (value, range, decimals, type), panels (text), boolean toggles, value lists (selected items), color swatches; SHA-256 digest fallback for any other persistent-typed param holding internalized data. Schema v2.

**Diff engine** (`4da6575`) — `DocumentDiff.Compute(from, to)` returns categorized lists (`ObjectsAdded` / `ObjectsRemoved` / `ObjectsModified`) with `ObjectChangeKind` flags (Renamed / Moved / WiresChanged / PersistentChanged) and per-change human-readable summaries.

**Drawer inspector** (`9d310d9`) — Each non-current commit's drawer surfaces a "Changes from this version to current" section with categorized lists and short summaries. Lazy-loaded on first expand; the right-hand-side current `.gloom.json` is parsed once per panel refresh.

**Schema progression for the canvas overlay** (`cd8d84b`, `e358585`):
- v3 — `CanonicalObject.Bounds` so deletion ghosts can render at accurate size.
- v4 — `PersistentData.ValueListItems` (full Name + Expression list) so value-list content edits surface.
- v5 — `PersistentData.ValueListMode` so DropDown / CheckList / Sequence / Cycle changes surface.

All optional fields with `= null` defaults; older documents parse cleanly and older plugin builds ignore additions.

**On-canvas diff overlay** (`a48f456`) — Singleton paints highlights atop GH canvas: green halos for added, yellow for modified, blue for moved-only, red kind-aware ghosts for deleted (the deleted slider's track + value, the deleted panel's text, the deleted swatch's actual color, etc.). Movement trail = dashed translucent old-bounds rect + solid arrow from old center to new center. Persistent ghost-below per kind (slider track + knob + range labels with orange-when-changed; panel hover preview ABOVE for wired panels only; color swatch with HSVA readout; value list multi-line summary). Wired-panel filter — standalone panels are scratchpad notes, ignored. Toggle in the panel + four action filters (Added / Modified / Moved / Deleted) + Hover-for-details mode (halos always; extras on hover only). Throttled diff recompute (250ms). Defaults to ON.

**Compare-against-any-commit + right-click restore + missing-wire arrows** (`709017a`, `27e3c65`, `541565e`):
- Per-row `◎` / `◉` button sets that commit as the overlay's `ComparisonReference`. Reference label + Reset button in the panel header. File/repo switch resets to HEAD; in-place edits do not.
- Win32 NativeWindow intercepts `WM_RBUTTONDOWN` so right-clicking a ghost shows our single-item restore menu and suppresses GH's normal context menu (non-ghost right-clicks pass through unchanged). Restore actions cover modified content (slider/panel/boolean/color), moved (pivot), and deleted (recreate via `Instances.ComponentServer.EmitObject`, restoring InstanceGuid, pivot, input + output param GUIDs, persistent state, and reconnecting input wires whose source still exists). All wrapped in `doc.UndoUtil.RecordEvent`.
- Red dashed bezier from each missing source to the consumer's specific input port (uses `InputGrip` / `OutputGrip` for per-port targeting). Arrow persists until ANY source is plugged into the input, not just the originally-intended one. Solid green bezier overlays each NEW wire (in to-doc but not from-doc) on top of GH's wire path. Per-output-port anchors distribute along ghost edges so multi-output deleted components render distinct arrow starts.

**Same-name value-list expression-edit detection** (`e74e811`, refined in `61190b1`) — `ValueListItem.Canonicalize` strips any trailing-letter suffix run (covers GH's L/D/F/M rewrites and combos thereof) and normalizes trailing-zero scale, so `5`, `5L`, `5.0`, `5.00`, `5.0d` all canonicalize to the same form. `ExpressionsEquivalent` then suppresses the diff when neither side parses as a clean number — GH normalizes opaque non-numeric text in unpredictable ways (observed in the wild: `2A² - 1` silently becoming `2² - 1` between sessions, likely from stripping unbound variable references), and pure text-to-text drift can't be reliably distinguished from real edits. Numeric edits (`5` → `10`) and edits crossing the numeric boundary (`5` → `"hello"`) still surface as `~N expression`.

**Structured ghosts for MD slider + gradient** (`3d0dfe1`, `48ec6a8`, schema v6 from `04aa91f`):
- **MD slider** — read via the typed `GH_MdSlider` handler (free-floating param path); ghost is sized to match the live component bounds exactly so the 2D dot lands where the live triangle would for the OLD (X, Y). Y axis is flipped at paint time to match GH's bottom-left-origin convention.
- **Gradient** — `GH_GradientControl` is an `IGH_Component` (not a free-floating param), so the persistent capture is routed through `SerializeComponent` as well. `GH_Gradient` exposes stops via an indexed `Grip(int)` accessor paired with `GripCount`, and each `GH_Grip` stores its colour as `ColourLeft`/`ColourRight` (read either; prefer Left). Reflection walk is strictly read-only — properties and fields only, NEVER method invocation, after a brute-force method walk corrupted GH's drawing pipeline in an earlier iteration (see `feedback_no_reflection_method_invocation.md`).
- **Gradient bar paint** — single `LinearGradientBrush` with `InterpolationColors` (ColorBlend) covers the whole bar, replacing the per-segment stitching that left sub-pixel seams as faint vertical white lines. `was: N stops` label sits below the rect in the dark brown-olive used by all other ghost labels.

**Deferred Phase 3 items** (acknowledged limitations):
- Restoring downstream consumer wires when restoring a deleted component — visible via missing-wire arrows; the user manually rewires (the visualization is honest and predictable).
- Restore-on-add (deleting a newly-added component) — not implemented; UX risk of accidentally trashing work.

### Phase 4 — First pass landed (built clean, awaiting smoke test on Windows)

**Team collaboration: remotes, push/pull/fetch, upstream tracking, smart Sync.** Implementation merged locally and the macOS-side `.gha` is in `~/Library/.../Libraries/G-Loom/`. **Not yet deployed on Windows and not yet smoke-tested.**

**Next session: start with the Windows smoke test.**

1. **Deploy on Windows first** — close Rhino, run `build/deploy-local.ps1`. The macOS deploy is already done from the previous session but the user's real Rhino is on Windows; that's where actual exercise happens.
2. Open a tracked `.gh` → confirm two new rows appear in the panel after `Branch:`:
   - `Remote:` button showing `(no remote) ▼`
   - `Sync:` row showing `(no remote configured)` with a disabled Sync button
3. Click `Remote ▼` → `Add remote...` → name `origin`, paste a real URL.
4. Remote button should now read `origin (no upstream) ▼`; Sync button label should switch to **Push**.
5. Click **Push** — first push auto-sets upstream via `-u`. After it succeeds the Remote button should show `origin/<branch>` and the Sync row should read `✓ up to date`.
6. Commit something locally → Sync flips to `↑1` / **Push**. Push it. Verify it appears on the remote.
7. From another clone (or by editing on the remote and pushing), get the local copy behind. Sync should show `↓N` / **Pull** and pulling should swap the canvases.

**What was built (high-level):**

- **Repository layer** (`Vcs/GLoomRepository.cs`):
  - Remote CRUD: `GetRemotes`, `AddRemote`, `RemoveRemote`, `SetRemoteUrl`
  - Upstream: `GetUpstream`, `SetUpstream`
  - Ahead/behind: `GetAheadBehind` via `rev-list --left-right --count` (local-only, no network)
  - Network: `Fetch`, `Pull` (ff-only), `Push` (with `-u` toggle) — each returns a `NetworkResult(success, message)` rather than throwing, so the panel can render failure text
  - New `RunNetwork` helper sets `GIT_TERMINAL_PROMPT=0` so credential prompts fail fast instead of hanging Rhino on a TTY-less stdin

- **Panel** (`Ui/GLoomPanel.cs`):
  - Two new rows in the metadata block right after Branch: `Remote:` (button-as-dropdown) and `Sync:` (status label + action button)
  - Remote dropdown morphs with state: "Add remote..." when empty; otherwise URL info rows, "Set upstream..."/"Change upstream..." (one-click when single remote), "Change URL...", "Add another...", "Remove..."
  - Sync button label switches between **Sync / Push / Pull** based on `(ahead, behind)`; counts render as `↓N ↑N`
  - Smart Sync runs fetch → ff-only pull (if behind) → push (if ahead); first push auto-sets upstream; non-ff pull surfaces an explicit "real merges land in a future phase" hint
  - After a successful pull, `DocumentTracker.ReloadAllInRepo` is called so open canvases reflect the new working tree without a manual close/reopen

**Known limits left out of Phase 4 by design (confirmed with user):**

- **Network ops are synchronous on the UI thread** — Rhino will hang briefly during fetch/push on slow networks. Async + cancel UI is a polish item for later.
- **Pull is fast-forward-only.** Diverged branches surface a clear rejection message; real merges with on-canvas conflict UI land in **Phase 5**.
- **Credentials are entirely delegated to git's helpers** (Windows Credential Manager, macOS Keychain, SSH agent). No custom credential UI.
- **Single-remote scope.** Multiple remotes still work mechanically (the menu handles N), but UX is optimized for the one-remote case the user said is dominant.

### Beyond Phase 4

See README's Roadmap section. Roughly: merge with on-canvas conflict UI (Phase 5) → scoped branches with promote/refresh (Phase 6) → LFS for heavy geometry (Phase 7) → audit, distribution, polish (Phase 8).

## Pointers to the user's persistent memory

The user's memory under `C:\Users\samac\.claude\projects\D--Code-Projects-G-Loom\memory\` has narrower documents. This `CLAUDE.md` is the synthesis; the memory files are the receipts.

- `MEMORY.md` — index, one-line hooks
- `user_platform.md` — user is on Windows; PowerShell default; canonical Grasshopper Libraries path
- `feedback_panel_only_ux.md` — no ribbon components
- `feedback_no_reflection_method_invocation.md` — properties + fields only when reflecting over GH SDK objects; method invocation broke the drawing pipeline once
- `project_target_workflow.md` — AEC team primary, Product secondary, solo a subset of team
- `project_recipe_versioning.md` — the thesis in detail with friction points
- `project_branches_are_systems.md` — system-vocabulary rationale
- `project_three_mode_scope.md` — the three modes, what each centers
