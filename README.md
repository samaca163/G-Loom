# G-BIM

Git-style version control for Grasshopper 3D.

A successor in spirit to [GGit](https://github.com/KaivnD/GGit) (dormant) and a cross-platform, open-source companion to [BranchHopper](https://branchhopper.com/). The goal: bring legible commits, branches, diffs, and merges to Grasshopper definitions, with the diff visualized directly on the canvas.

## Status

**Phase 0 — scaffolding.** A loadable `.gha` that registers a single diagnostic component and attaches the canvas paint hooks we will later use for diff overlays. No Git logic yet.

## Requirements

- Rhino 8 on macOS or Windows
- .NET SDK 7 or 8 (for building)

## Build

```sh
~/.dotnet/dotnet build src/GBim.Plugin/GBim.Plugin.csproj -c Release
```

## Deploy locally (macOS, Rhino 8)

```sh
./build/deploy-local.sh
```

This copies the built `GBim.gha` to:

```
~/Library/Application Support/McNeel/Rhinoceros/8.0/Plug-ins/Grasshopper (b45a29b1-4343-4035-989e-044e8580d9cf)/Libraries/G-BIM/
```

Restart Rhino, open Grasshopper. You should see a `G-BIM` ribbon tab with a single `G-BIM Status` component, and on first canvas paint Rhino's command line will print:

```
[G-BIM] First canvas paint via CanvasPostPaintObjects — overlay machinery is wired up.
```

## Roadmap

- **Phase 0** — load a `.gha`, register a placeholder component, attach canvas paint hooks. *(current)*
- **Phase 1** — canonical JSON serializer over `GH_IO`; LibGit2Sharp wired up; commit + log + checkout; canvas overlay paints add/remove/modify diff against HEAD.
- **Phase 2** — branching, custom merge driver, conflict resolver UI on canvas.
- **Phase 3** — remotes, LFS for embedded geometry, auto-commit on save.
- **Phase 4** — Yak distribution, signed/notarized macOS bundle.

## License

MIT — see [LICENSE](LICENSE).
