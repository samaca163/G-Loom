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
The user is on **Windows** (was on macOS earlier in the project's life — see `memory/user_platform.md`). PowerShell deploy script is what they use day-to-day. The macOS deploy script is maintained but the user can't easily test it; if you change deploy logic, change both scripts in lockstep.

Tools available on the machine: `dotnet` 8.0.200 at `C:\Program Files\dotnet\`, `git` at `C:\Program Files\Git\cmd\git.exe`. PowerShell is the default shell.

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

### Phase 1 — Done (commits up through `bb479f0` and the rename commit that follows this file)

- Loadable `.gha` (cross-platform, Windows + macOS Rhino 8)
- Canonical structural JSON serializer (`<name>.gloom.json` alongside the `.gh`)
- Eto panel docked + auto-active, with white "G" tab icon
- Auto-versioned commits (`<basename>_V###`) via system `git`
- Restore (file-only `git checkout <sha> -- <files>`, doesn't move HEAD)
- Multi-file repo isolation (each `.gh` sees only its own commits in the panel)
- Tab-switch sync (panel tracks active document)
- Branch ops: list/create/switch/delete with system-aware UI
- Reload-all-in-repo on branch switch (originally-active doc stays active)
- Paired `.gh + .gloom.json` blob fingerprint for "current commit" arrow
- Cross-platform deploy scripts

### Phase 2 — Underway (next concrete work)

- **Tags** (lightweight): list / create / delete via the panel
- **Toolchain metadata** captured at every tag (Rhino + GH + plugin versions)
- **Per-tag metadata schema** that supports submittal info (Mode 1), product cert (Mode 2), and release notes (Mode 3)
- **Branch-base markers** in history (where the current branch forked from its parent)
- **Branch rename**

### Beyond Phase 2

See README's Roadmap section. Roughly: visual diff (Phase 3) → team collaboration (Phase 4) → merge with on-canvas conflict UI (Phase 5) → scoped branches with promote/refresh (Phase 6) → LFS for heavy geometry (Phase 7) → audit, distribution, polish (Phase 8).

## Pointers to the user's persistent memory

The user's memory under `C:\Users\samac\.claude\projects\D--Code-Projects-G-BIM\memory\` has narrower documents. This `CLAUDE.md` is the synthesis; the memory files are the receipts.

- `MEMORY.md` — index, one-line hooks
- `user_platform.md` — user is on Windows; PowerShell default; canonical Grasshopper Libraries path
- `feedback_panel_only_ux.md` — no ribbon components
- `project_target_workflow.md` — AEC team primary, Product secondary, solo a subset of team
- `project_recipe_versioning.md` — the thesis in detail with friction points
- `project_branches_are_systems.md` — system-vocabulary rationale
- `project_three_mode_scope.md` — the three modes, what each centers
