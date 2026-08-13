# G-Loom — History, Industry Position, and Strategic Direction

*Written 2026-08-13. Research base: full repo history, plus ~40 primary sources (McNeel forum, vendor sites, funding databases, academic papers) verified August 2026. This document is the project's strategic reference; the README carries the public-facing summary.*

---

## Part I — The story so far

### Timeline

| Era | Dates | What happened |
|---|---|---|
| **0. Inception as G-BIM** | 2026-05-04 | Plugin skeleton + the canonical document serializer (Phase 1a). Three diagnostic canvas components (later purged — the origin of the panel-only rule). |
| **1. Phase 1 — the foundation** | 05-08 → 05-09 | Commit / log / restore with auto-versioned messages (`<name>_V###`). LibGit2Sharp abandoned for the system `git` CLI within the first hour of use (macOS native-library failure) — the single most consequential architecture decision. Multi-file repo isolation, blob-pair "current version" fingerprint, in-place restore reload, both deploy scripts. v0.1.0. |
| **2. The rename** | 05-09 | G-BIM → **G-Loom**. Plugin and panel GUIDs deliberately preserved. |
| **3. Phase 2 — branches, tags, pinning** | 05-09 (same evening) | Branch ops with system-vocabulary UI, fork-point markers, per-commit drawer, tags with **toolchain pinning** (Rhino/GH/RiR/G-Loom versions embedded in annotated tag messages) and the mode-aware metadata schema (AEC / Product / Release). |
| **4. Phase 3 — the visual diff** | 05-09 → 05-11 | Persistent-value capture (schema v2→v6), the diff engine, and the **on-canvas overlay**: halos, kind-aware ghosts (slider tracks, panel text, swatch colors, gradients, MD sliders), movement trails, missing-wire and added-wire beziers, compare-against-any-commit, right-click restore of values/positions/deleted components. |
| **5. Cross-platform hardening** | 05-14 → 05-16 | macOS load fix (NativeWindow gating), ghost rendering refinements, expression-diff noise suppression. |
| **6. Phase 4 — remotes & sync** | 05-19 | Remote CRUD, upstream tracking, ahead/behind counts, smart Sync (fetch → ff-pull → push) in the panel. |
| **7. The narratives preview** | 06-18 | On a side branch: the commit dialog (title + description, diff-generated draft, `Gloom-Version:` trailer) shipped as a GitHub **pre-release**, plus the **element-versioning / Cimbra design doc** (see Part V). |
| **8. v0.2.0 — performance overhaul** | 08-13 | 22 commits: the post-save freeze (up to ~200 git spawns per edit) reduced to zero for ordinary edits; the overlay recompute moved out of the paint handler; pooled GDI+ resources; a native handle leak and a real pipe-deadlock class fixed; panel reads moved off the UI thread. Multi-agent adversarial review caught 5 introduced bugs pre-release. |
| **9. v0.2.1 — hotfix + dialog** | 08-13 | Version labels resolve from the `Gloom-Version:` trailer (fixing history display for preview-build users); the commit dialog ported to main; staging hardened (non-ASCII paths, exception-safe unstage). Current release. |

**Shape of the effort**: 77 commits, ~7,500 lines of C#, one author, five dense working days over fourteen weeks. Three files (panel, overlay, repository) hold 65% of the code. No automated tests. The repo is private; three releases exist.

### The decisions that define the project

1. **Version the recipe, not the result.** Geometry is a side effect; the graph is the artifact. Diffs describe *design moves*; history is a chain of decisions, not a flipbook of frames.
2. **Branches are systems** — substitutable design strategies (envelope options, product variants), not detours. The UI speaks system-vocabulary.
3. **Three modes, one substrate**: AEC parametric design (primary), product variants (secondary), tool/library development (tertiary). Don't optimize one and break the others.
4. **Toolchain pinning at every tag** — the answer to "the recipe no longer runs in 2032."
5. **Panel-only UX**; system git over LibGit2Sharp; read-only reflection over the GH SDK; additive-only schema evolution; comments record the WHY.
6. **Storage ≠ structure** (June design): content-addressed blobs can return bytes but can't answer "how many panels changed" — queryable structure must be a small, text, git-lane artifact.

---

## Part II — Where G-Loom stands today

**Working, shipped (v0.2.1)**: auto-versioned commits with a title/notes dialog and diff-generated drafts; file-scoped history with restore; branch operations; tags carrying toolchain + submittal metadata; the canonical JSON serializer (schema v6: structure, wires, groups, sliders/panels/toggles/value-lists/colors/gradients/MD-sliders, digest fallback); the on-canvas diff overlay with per-kind ghosts, wire arrows, any-commit comparison, and right-click restore; remotes/push/pull with smart Sync; fast (post-overhaul, ordinary edits cost zero git activity).

**Missing / deferred**: merge (pull is ff-only), scoped branches (designed, unbuilt), heavy-geometry storage (design superseded by DVC/Cimbra, no code), element versioning (design only), automated tests (none), async network UI, Yak/food4rhino distribution, GH2 support.

**Known unknowns**: the Phase 4 + v0.2.1 Windows smoke-test pass is still pending; Grasshopper 2's built-in comparison (below) has not been examined hands-on.

---

## Part III — The industry, as of August 2026

### Direct competition: the category is empty in practice

| Tool | Status | What it lacks vs G-Loom |
|---|---|---|
| **BranchHopper** (TH-OWL thesis → 3-person team) | Latest release 2025-08-02, ~12 months stale; time-expiring builds | No branches, no remotes, no merge, no toolchain pinning, Windows-only, GH1-only. Absent from the July 2026 forum thread asking for exactly this. |
| **SwarmSync** (Parametric Zoo) | ~87 downloads; reported crashing Rhino 8 | GitHub sync without semantic diff |
| **DefinitionLibrary** | Warmly received beta | A cluster/snippet *library* — reuse axis, not diff/branch/merge. Plausible partner. Price anchor: "≤ $20/seat/yr" |
| Githopper, GGit, GHShot (academic), ATWD (hackathon) | All dead | — |

Two telling facts: **no version-control tool for Grasshopper is on the Yak package registry at all** (1,176 packages, 7.5M downloads — the category shelf is empty), and the July 2026 McNeel forum thread asking for GH file comparison received suggestions of "use GH2" and "diff .ghx by hand" — no shipped tool was even mentioned. Demand is chronic (5+ years of threads, three peer-reviewed papers, a Foster+Partners/Grimshaw hackathon prototype) but low-heat: a persistent ache, not a bleeding neck.

**The Pancake lesson** — the one developer who previously built layout-independent GH document comparison *removed it* from his plugin, concluding that "merely pointing out changes doesn't help much" without merging. Kactus (git-for-Sketch) died on the same hill. **Diff is the demo; merge is the product.**

### Speckle: complement, not competitor

Speckle ($19M raised, Series A Dec 2024, Arup/Jacobs/Mott MacDonald) versions the **data flowing out of** a definition — its GH connector publishes geometry snapshots; it has no concept of the graph, wires, or component identity, and its 2026 roadmap points at data warehousing, BI, and governance. The clean framing: **Speckle versions what came out; G-Loom versions what made it.** The cheap, aligned integration: stamp the G-Loom commit SHA + toolchain pin into Speckle version metadata so every published model traces to its exact recipe.

### The proof point and the graveyard

- **Onshape** made git-style branch/merge a headline feature of parametric CAD and was acquired by PTC for **~$470M** (2019); it remains one of PTC's growth engines. Versioned parametric design is demonstrably valuable — in the adjacent market.
- But **every independent "git for designers" company died or exited small**: Abstract raised $46M, pivoted, sold only its documentation module to Adobe, and its domain no longer resolves; Plant shut down; Kactus went quiet; Plastic SCM exited to Unity for ~$20M; Snowtrack was absorbed by Perforce. The survivors made version control a **platform feature** (Figma branching = Enterprise tier) — value accrues to the platform.

### The platform risk: Grasshopper 2

As of **July 30, 2026**, GH2 is a beta feature inside the Rhino 9 Beta. Native format **`.ghz` (binary)**; **no `.ghx` export**; GH1 plugins do not load; the GH2 SDK is still taking breaking changes. A McNeel staffer stated publicly (July 2, 2026): *"One can compare versions in Grasshopper 2."* Unverified scope — it may be snapshot comparison, not semantic graph history.

Implications: GH1 remains the production environment for years (firms' IP is in `.gh`; no GH2 plugin ecosystem exists), but `.gh`-parsing is a **depreciating asset**, and the platform owner may commoditize basic diff. The durable assets are the ones a format transition cannot erase: the canonical recipe schema, the diff/merge model, branches-as-systems, toolchain pinning, and the git substrate.

### The AI wave — the fact that changes the thesis

- **McNeel now ships an official MCP server** letting Claude, Copilot, Codex and local LLMs *build and edit Grasshopper definitions live*.
- **Raven** (AI-for-GH, food4rhino-promoted webinars Jan + Apr 2026) charges **$49–99/seat/month** — the first demonstrated willingness-to-pay in GH tooling that isn't revenue-unlocking.
- The **CHI '26** study of CAD version-control pain ranks **"AI-assisted change summaries"** as design opportunity #1 ("users still have no way to know *why* changes were made").
- **GhJSON** (Feb 2026, open source, spun out of SmartHopper) already claims the "JSON standard for Grasshopper" ground, pitched for "AI analysis, version control, and sharing."

Agentic editing turns reviewable diffs, rollback, and provenance from conveniences into **safety requirements**. G-Loom has already built precisely that machinery — the overlay is an agent-edit review screen, right-click restore is selective rejection of an agent's change, the trailer format is provenance. Nobody in the AI-for-GH cohort has a history/rollback story.

---

## Part IV — Viability, honestly

**As a free community tool: viable, with a known ceiling.** Real, chronic, well-documented demand; an empty category shelf; realistic trajectory of low-thousands of installs and reference-tool status among the 1,000–5,000 computational-design teams worldwide (MetaHopper-shaped adoption, ~100k lifetime downloads over years, not eleFront's 435k). This path also carries the ecosystem's proven personal exit: reputation (Kangaroo's author is at Foster + Partners; MetaHopper's leads computation at NBBJ).

**As a paid version-control plugin: not a business.** Eight attempts in ten years, zero paying customer bases; ~12,000 active GH customers (McNeel's last public figure) of which the multi-editor-team slice is low thousands; a $20/seat/yr anchor; no in-Grasshopper purchasing rail; the informed community's default answer is a free workaround; and the firms with the worst pain build their own. Multiplying plausible seats by the plausible price yields a $10k–100k/yr business — before GH2 forces a rewrite.

**As a venture: unproven but real, on one condition** — that the product is not "version control" but the **decision-record and safety layer** described below, where the AI-era buyers and the archival/liability buyers exist and the pricing anchors are Raven ($49–99/seat/mo) and Speckle Team ($99/mo), not the plugin shelf. The honest posture: build to maximize option value. The floor (reputation, category ownership, hireability) is guaranteed by shipping well; the ceiling (a company) requires the AI leg or the element-versioning leg to find paying teams, which only real users can prove.

---

## Part V — The course from here

### The thesis, evolved

> **G-Loom is the record of decisions for parametric design.**

Same substrate, three legs:

1. **For teams** — system versions, visual diff, and *assisted merge*: the collaboration story (the leg every predecessor died without).
2. **For decades** — toolchain pinning and reproducible deliverables: the archival/liability story. This is the best-evidenced unmet pain in the ecosystem: McNeel's own Package Restore explicitly installs "the latest stable version" when the exact one is missing — the precise failure mode behind the largest cluster of "my old definition is broken" threads. No competitor touches it.
3. **For the AI era** — the review, rollback, and provenance layer for agentic Grasshopper editing: every AI edit becomes a diffable, revertible, explainable commit. The venture-shaped leg.

Positioning language: never "git for Grasshopper." Say **system versions**, **design options**, **the project's memory**. Never surface git vocabulary beyond what the panel already shows (the winners — Figma, Unity VC — hid the git).

### Roadmap (replaces the old Phase 5–8)

- **Phase 5 — De-risk & harden** *(now)*
  - Hands-on GH2 verification in the Rhino 9 Beta: what does its "compare versions" actually do? (Determines how hard to lean on diff vs merge/pinning in positioning.)
  - **Format-agnostic core**: introduce an adapter seam so the canonical JSON, diff engine, branch model, and pinning are independent of the GH1 file format; `.gh` is one reader, `.ghz` becomes another when the GH2 SDK stabilizes.
  - **GhJSON interop**: export/import at the boundary, engage publicly in the thread; keep the richer internal schema. Adopt the community's language instead of fighting a format war.
  - Complete the Windows smoke-test pass (Phase 4 + v0.2.1); fix what it finds.
- **Phase 6 — Assisted merge** *(the gate feature)*
  Three-way, on-canvas, **assisted** — not textual, not fully automatic: pick a branch to merge; both sides' diffs vs the merge-base render with the existing overlay machinery; non-conflicting changes apply automatically; conflicts resolve per-component with take-left / take-right (the existing right-click restore primitives already do exactly this); the result commits with a merge trailer. Ship the 80% case: value conflicts, independent additions/deletions, wire changes on distinct inputs.
- **Phase 7 — Launch** *(the go-public gate)*
  Gate checklist: Phase 5 + 6 done · smoke-tested on both platforms · Yak package + food4rhino listing · demo GIFs of diff/restore/merge · README + a minimal site · GhJSON statement · repo public. Launch into the empty registry shelf as the category's first occupant, with the McNeel forum threads answered directly.
- **Phase 8 — AI layer** *(the venture experiment)*
  AI commit narratives (upgrade the dialog's deterministic draft with an LLM); an MCP-aware review flow ("an agent edited this definition — here is the diff, accept/revert per component"); provenance stamping into Speckle version metadata. This phase is where paying teams either appear or don't — instrument it honestly.
- **Phase 9 — Element versioning / Cimbra**
  The June design, now on the official roadmap: recipe → elements → a queryable quantities-and-qualities timeline (git lane for structured element extracts, DVC/Drive lane for display geometry, per the three-lane Cimbra contract). This is the long-term move up the value chain toward what firms already pay for.
- **Demoted, not deleted**: scoped branches + promote/refresh move post-launch (the performance case for them shrank after v0.2.0; the demo case remains). git-LFS is formally replaced by the DVC/Cimbra design.

### Risk register

| Risk | Posture |
|---|---|
| GH2 ships semantic version comparison | Verify now; if real, lean positioning on merge + pinning + provenance (which platform-level snapshot diff doesn't touch) |
| `.gh` format obsolescence | Adapter seam (Phase 5); canonical schema is the durable asset |
| A funded/named entrant (e.g., the ex-Grimshaw ATWD lead's rumored tools) | Speed to the empty registry shelf; the substrate depth (pinning, restore, overlay) is months ahead of any snapshot tool |
| McNeel gives the category away for free | Same as GH2 risk; also: McNeel historically platform-izes (MCP, Yak) rather than verticalizes — integration posture beats confrontation |
| Solo maintainer, no tests | Add a test project for the pure logic (serializer, diff, versioning, trailer parsing) in Phase 5; it's also launch hygiene |
| Merge complexity explodes | Scope discipline: assisted merge of the 80% case; never promise auto-merge |

---

## Part VI — Sources

Key primary sources (all verified August 2026): McNeel Discourse threads [128645](https://discourse.mcneel.com/t/version-control-for-gh/128645), [134142](https://discourse.mcneel.com/t/annoyances-and-tips-for-grasshopper-version-control-development/134142), [220526](https://discourse.mcneel.com/t/understanding-old-gh-code-compare-two-versions-of-a-gh-file/220526), [221336 (GH2 beta)](https://discourse.mcneel.com/t/rhino-beta-feature-grasshopper-2-beta/221336), [219944 (ghx in GH2)](https://discourse.mcneel.com/t/file-ghx-in-g2/219944), [215646 (GhJSON)](https://discourse.mcneel.com/t/ghjson-a-json-standard-for-grasshopper/215646) · [BranchHopper](https://branchhopper.com/) · [Speckle docs + updates](https://speckle.systems/updates/) · [Onshape branch/merge](https://www.onshape.com/en/features/branch-merge-cad) + [PTC acquisition](https://www.ptc.com/en/news/2019/ptc-to-acquire-leading-saas-product-development-platform-provider-onshape) · [Rhino MCP Platform](https://mcneel.github.io/RhinoMCP/) · [Yak registry index](https://rhinopackages.github.io/) · [Package Restore behavior](https://developer.rhino3d.com/guides/yak/package-restore-in-grasshopper/) · GHShot papers ([eCAADe 2019](https://papers.cumincad.org/data/works/att/ecaadesigradi2019_397.pdf), [AiC 2021](https://www.sciencedirect.com/science/article/abs/pii/S0926580521002533)) · [CHI '26 CAD versioning study](https://arxiv.org/abs/2602.09236) · [Adobe/Abstract](https://blog.adobe.com/en/publish/2022/01/10/adobe-acquires-abstracts-notebooks) · [Figma branching](https://www.figma.com/blog/how-and-why-we-built-branching/) · [Karamba3D pricing](https://buy.karamba3d.com/collections/licenses) · [ShapeDiver pricing](https://www.shapediver.com/pricing) · [Rhino product analysis (Ronald 2020)](https://medium.com/spatiomatics/killer-product-a-rhino3d-product-analysis-2f90ebfd9465) · [AEC Magazine GH numbers](https://aecmag.com/news/rhino-grasshopper/).
