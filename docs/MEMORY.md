# G-Loom — Memory (living handoff)

> Durable record so any session — on this machine or one that pulls from GitHub — can resume.
> Newest entry first. If this file and the code disagree, **the code wins** (fix this file).

---

## 2026-09-02 — MCP commit/tag debugging + hardening (`experiment/mcp`)

### ⚠️ Read first — the environment is shared and unstable
- **Multiple opencode sessions share the SAME local working tree.** One session's edits get
  clobbered by another. This session's code fixes were **reverted by a re-clone/reset** mid-session
  (reflog: fresh `clone` + `checkout experiment/mcp`). **Always re-verify a fix is present before
  relying on it — do not assume it survived a re-clone or another session's commit.**
- The tree is now a **fresh clone** of GitHub `experiment/mcp`, and the MCP layer was **refactored**
  since some work below: tools are in `Mcp/Tools/Memory/{MemoryTools,RecordTools,VersionTools}.cs`,
  host in `Mcp/Host/`, protocol in `Mcp/Protocol/`, state in `Mcp/State/`. Older names
  (`Tools-Memory.cs`, `CommitTag.cs`, `TagMetadata.cs`, `TagTools.cs`) map to these new files.

### What this session diagnosed and fixed (re-verify each is still present)
1. **MCP `gloom_commit` did not refresh the panel** (stuck "Unversioned / Untracked").
   - Root cause: the panel only recomputes on *document* change events; a pure git commit fires none.
   - Fix: after a successful MCP commit, call `DocumentTracker.Instance.Refresh()` (force recompute
     past the stat-key memo). Where: the MCP commit tool's success path.
2. **MCP commits were missing the `Gloom-Agent:` / `Gloom-Intent:` trailers.**
   - Root cause: the MCP commit path called `GLoomRepository.CommitStaged` directly (which only appends
     `Gloom-Version:`), bypassing the `gloom_commit` tool's trailer logic.
   - Fix: stamp `Gloom-Agent: gloom-mcp` + `Gloom-Intent: <tool|intent>` in the MCP commit path.
3. **`gloom_tag` notes were being dropped.**
   - Root cause: the `notes` param was captured but never written into the tag message.
   - Fix: store `notes` into `TagMetadata.Notes` before serializing. Added regression test
     `Commit_Tag_StoresNotes`.
4. **Root cause of the recurring "needs Rhino reload after deploy": a STALE duplicate assembly
   shadowed the new build.**
   - `%APPDATA%\Grasshopper\Libraries\G-Loom\` held BOTH `GLoom.gha` (new) and an old `GLoom.dll`.
     Grasshopper loaded the stale one, so correct fixes "didn't work until reload."
   - Fix: hardened `build/deploy-local.ps1` to auto-remove stray assemblies (any `GLoom.*` that
     isn't the freshly-built `GLoom.gha`) after copying, so deploys can't be silently shadowed.

### Learnings to carry forward
- **"Recompute after commit" is the invariant.** Every commit path (panel dialog AND MCP tool) must
  end in `DocumentTracker.Refresh()` (force) so panel + overlay track the new HEAD.
- **Stale duplicate assemblies are a first-class deploy hazard.** Only `GLoom.gha` may sit in the
  Libraries folder; the deploy script now enforces that.
- **Trailer conventions:** `Gloom-Version:` (auto), `Gloom-Agent:` (identity), `Gloom-Intent:`
  (action). All commit paths should stamp them.
- **Tag metadata stores free-text `notes`** in the tag message JSON.
- **Panel debounce is 250 ms** (`CommitDialog._debounceMs`) — a smoke test that polls too fast will
  read "Unversioned" transiently.
- **Concurrency is the top risk to losing work** — verify, then commit promptly.

### State at end of session
- Branch `experiment/mcp`, version `0.3.0-mcp.2` (pre-release).
- Tests: 94/94 passing as of this session (GLoom.Core.Tests + GLoom.Survey.Tests) — re-run to confirm.
- Deploy: hardened `build/deploy-local.ps1`; `GLoom.gha` (0.3.0-mcp.2) deployed; stray `GLoom.dll` removed.

### Resume checklist
1. **Re-verify the 4 fixes above are actually in the tree** (a re-clone may have reverted them);
   re-apply if missing (details above).
2. **Smoke test in Rhino (Windows):** `gloom_commit` → panel auto-advances to the new `V###` with no
   reload; the commit carries `Gloom-Agent`/`Gloom-Intent`; `gloom_tag` with `notes` → notes present
   in the tag message.
3. **Phase 5 (AGENTS.md):** Grasshopper 2 / Rhino 9 Beta verification; format-adapter seam;
   GhJSON interop; the pending Windows smoke-test pass; test coverage for the pure logic.
4. **Coordinate sessions** on the shared working tree.
