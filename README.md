# G-BIM

Git-style version control for Grasshopper 3D.

A successor in spirit to [GGit](https://github.com/KaivnD/GGit) (dormant) and a cross-platform, open-source companion to [BranchHopper](https://branchhopper.com/). The goal: bring legible commits, branches, diffs, and merges to Grasshopper definitions, with the diff visualized directly on the canvas.

## Status

**Phase 1 — partial (v0.1.0).** Working commit / log / restore over a real Git repo, exposed through an Eto panel docked next to the Grasshopper canvas. Already shipped:

- Canonical, line-diff-friendly JSON serialization (structural — components, params, wires, groups).
- Auto-versioned commit messages (`<base>_V###`) per .gh, derived from `git rev-list`.
- In-place Restore: writes the chosen commit's content to disk and reloads the live document, no manual close/open.
- "Current version" arrow re-derived from working-tree blob hashes — survives Grasshopper restarts with no side state.
- Multi-file repos isolated per .gh: each definition's panel sees only its own commits.
- Live tracking when switching between open .gh tabs.

Still pending in Phase 1: persistent value capture (slider values, panel text, internalized data) and the canvas diff overlay.

## Requirements

- Rhino 8 on macOS or Windows
- .NET SDK 7 or 8 (for building)

## Build

```sh
dotnet build src/GBim.Plugin/GBim.Plugin.csproj -c Release
```

The output `.gha` lands in `src/GBim.Plugin/bin/Release/net7.0-windows/GBim.gha`.

## Deploy locally (macOS, Rhino 8)

```sh
./build/deploy-local.sh
```

Copies the built `GBim.gha` to:

```
~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries/G-BIM/
```

## Deploy locally (Windows, Rhino 8)

```powershell
powershell -ExecutionPolicy Bypass -File build\deploy-local.ps1
```

Copies the built `GBim.gha` to:

```
%APPDATA%\Grasshopper\Libraries\G-BIM\
```

(Windows Rhino 8 stores third-party Grasshopper libraries here, alongside other `.gha` plugins — a different layout from macOS, where they live inside the Rhino plug-in folder.)

## After deploying

Restart Rhino, open Grasshopper. You should see a `G-BIM` ribbon tab with a single `G-BIM Status` component, and on first canvas paint Rhino's command line will print:

```
[G-BIM] First canvas paint via CanvasPostPaintObjects — overlay machinery is wired up.
```

## Roadmap

- **Phase 0** — load a `.gha`, register a placeholder component, attach canvas paint hooks. *(done)*
- **Phase 1** — canonical JSON serializer over `GH_IO`; commit / log / restore via the system `git` CLI; panel UI; canvas overlay paints add/remove/modify diff against HEAD. *(commit/log/restore + panel done; persistent value capture + canvas overlay pending)*
- **Phase 2** — branching, push/pull/fetch, custom merge driver, conflict resolver UI on canvas.
- **Phase 3** — remotes, LFS for embedded geometry, auto-commit on save.
- **Phase 4** — Yak distribution, signed/notarized macOS bundle.

## License

MIT — see [LICENSE](LICENSE).
