# G-Loom

**Weave parametric systems through time.**

G-Loom is version control for parametric Grasshopper definitions. Instead of versioning fat geometry files, it versions the *recipe* — the Grasshopper graph that produces them — so storage stays small, diffs are meaningful, and your project's history reads as a chain of design decisions, not a flipbook of frames.

Built on git, it scales from solo designer to multi-office team, and works equally well for AEC parametric systems, product variant management, and Grasshopper tool development.

## The shift

| Today | With G-Loom |
|---|---|
| Folders littered with `Tower_v1.gh`, `Tower_v1_dome_FINAL.gh`, `Tower_v1_dome_FINAL_actually-final.gh` | One repo with branches as systems: `envelope-mullion`, `envelope-unitized`, `structural-concrete-frame` |
| A Revit central file: ~800 MB per version | A parametric recipe: ~2 MB per commit; geometry regenerated or cached |
| Diff says: "all geometry is different" | Diff says: "this wire moved — the stair system flipped from circular to half-turn" |
| Compliance audit shows *what* was submitted | Compliance audit shows *why* — the system decisions that produced each submission |

## Built for three workflows

**AEC teams designing parametric buildings.** Branches are *system options* — substitutable strategies for an envelope, structural system, or MEP layout. Iterate on one envelope without recomputing the whole tower. Promote your chosen system to the project trunk. Tag DD / CD / IFC milestones with submittal metadata. Years later, the audit trail reads as a chain of reasoned system choices.

**Industrial and product designers.** Every variant lives on its own long-lived branch — sizes, materials, manufacturing routes. Sub-systems (mechanism, frame, seat) iterate as scoped branches. Tag releases for manufacturing handoff with the toolchain pinned, so the factory regenerates identical geometry from your recipe years later.

**Grasshopper tool / system developers.** Feature branches, semantic releases, real software-engineering-style version control for your analytical systems. Pin Rhino / GH / plugin versions on every tag so your sunlight analyzer still runs on Rhino 10 in 2032.

## Why the recipe, not the geometry

- **Tiny artifacts.** A Grasshopper definition is single-digit MB; a Revit central file is hundreds. Versioning recipes is 100×–1000× smaller.
- **Meaningful diffs.** Components added/removed/connected. Wires rerouted. Persistent values changed. The diff describes a *system move*, not a byte cliff.
- **Decision-grade history.** Each commit captures intent at a moment in time. The log is the chain of reasoning that produced today's systems — not snapshots.
- **Cross-tool durability.** With Rhino.Inside.Revit, one recipe drives both Rhino and Revit. One repo, one history, two outputs.

## Built on git

Branches, commits, merges, push/pull, distributed collaboration — decades of battle-tested substrate. G-Loom uses git for what it's great at and adds what parametric systems actually need on top: visual canvas diffs, scoped branches that internalize boundary geometry for fast sub-system iteration, promote/refresh round-trips between systems and the project trunk, LFS-cached snapshots at tagged milestones, and toolchain-version metadata that survives a decade of plugin churn.

## Status

Phases 1–4 shipped (v0.2.1): commit / log / restore with a commit dialog, branches with system-aware UX, multi-file repo isolation, tags with per-tag metadata and toolchain pinning, the on-canvas visual diff overlay (halos, ghosts, wire arrows, right-click restore, compare-against-any-commit), and remotes/push/pull with smart Sync. Currently in Phase 5 (de-risk & harden) on the road to assisted merge and public launch — see `docs/STRATEGY.md` for the full direction. Cross-platform (Windows + macOS Rhino 8). Free, MIT, open source.

## Install (from a GitHub Release)

1. Download `GLoom.gha` from the [latest release](https://github.com/samaca163/G-Loom/releases).
2. **Windows only:** right-click the downloaded file → Properties → check **Unblock** → OK. Windows marks downloaded assemblies; Grasshopper silently refuses to load a blocked `.gha`.
3. Copy it to your Grasshopper libraries folder:
   - Windows: `%APPDATA%\Grasshopper\Libraries\G-Loom\`
   - macOS: `~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries/G-Loom/`
4. Restart Rhino and open Grasshopper. You also need the system `git` CLI installed (on `PATH` or in the standard location for your OS).

## Requirements

- Rhino 8 on macOS or Windows
- System `git` CLI on `PATH` (or in the standard location for your OS)
- .NET SDK 7 or 8 (only for building from source)

## Build

```sh
dotnet build src/GLoom.Plugin/GLoom.Plugin.csproj -c Release
```

The output `.gha` lands in `src/GLoom.Plugin/bin/Release/net7.0-windows/GLoom.gha`.

## Deploy locally (macOS, Rhino 8)

```sh
./build/deploy-local.sh
```

Copies the built `GLoom.gha` to:

```
~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries/G-Loom/
```

## Deploy locally (Windows, Rhino 8)

```powershell
powershell -ExecutionPolicy Bypass -File build\deploy-local.ps1
```

Copies the built `GLoom.gha` to:

```
%APPDATA%\Grasshopper\Libraries\G-Loom\
```

## After deploying

Restart Rhino, open Grasshopper. The G-Loom panel auto-opens on first canvas creation, docked alongside whatever panel you have open (Properties / Layers / etc.). To reopen after closing, right-click any docked panel tab and pick "G-Loom" from the list. Rhino's command line should print:

```
[G-Loom] Plugin loaded.
[G-Loom] Panel registered (icon: 32x32).
```

## Roadmap

- **Phase 1 — Commit / log / restore foundation.** *(done)* Canonical JSON serializer over `GH_IO`; commit / log / restore via the system `git` CLI; Eto panel; auto-versioned commit messages; multi-file repo isolation; tab-switch sync; cross-platform deploy.
- **Phase 2 — Tags + toolchain metadata.** *(done)* Lightweight tags for milestones (DD / CD / IFC, software releases, product cert dates). Per-tag metadata schema (submittal info, release notes, toolchain pins). Branch-base markers in history.
- **Phase 3 — Visual diff & review.** *(done)* On-canvas diff overlay (add/remove/modify/move highlighted, per-kind ghosts, wire arrows, right-click restore), compare against any commit.
- **Phase 4 — Team collaboration.** *(shipped in v0.2.x; smoke-test pass pending)* Remotes (add/list/remove), push/pull/fetch, smart Sync, upstream tracking.
- **Phase 5 — De-risk & harden.** *(current)* Grasshopper 2 verification (Rhino 9 Beta); a format-adapter seam so the canonical recipe schema, diff engine, and branch model are independent of the GH1 file format; GhJSON interop; test coverage for the pure logic.
- **Phase 6 — Assisted merge.** Three-way, on-canvas assisted merge: both branches' changes vs the merge-base rendered with the existing diff overlay; non-conflicting changes apply automatically; conflicts resolve per-component (take left / take right) using the existing restore machinery.
- **Phase 7 — Launch.** Yak package, food4rhino listing, demo material, public repository.
- **Phase 8 — AI layer.** AI-written commit narratives; review-and-revert flows for agent-edited definitions (MCP); recipe provenance stamped into published model metadata (e.g., Speckle versions).
- **Phase 9 — Element versioning & heavy geometry.** Structured, diffable element extracts (quantities & qualities as a queryable timeline) on git; per-element display geometry content-addressed via DVC; three-lane project layout via the Cimbra scaffolder. Scoped branches + promote/refresh follow post-launch.

## License

MIT — see [LICENSE](LICENSE).
