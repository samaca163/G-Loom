# G-Loom — Memory (living handoff)

> Durable record so any session — on this machine or one that pulls from GitHub — can resume.
> Newest entry first. If this file and the code disagree, **the code wins** (fix this file).

---

## 2026-09-03 — rungs 3, 4 and 5 (`experiment/mcp`, `v0.3.0-mcp.4`)

### The thing to learn from this session
**Rung 3 had been finished on 2026-08-27 and was never committed.** ~100 KB of working,
unit-tested code sat untracked in a second clone at `D:\Code Projects\G-Loom` for a week while
this branch's notes said it was "next". It was recovered as the first act of this session
(backup branch `rescue/rung3-2026-08-27`, pushed before anything else, then cherry-picked).

**So: commit AND push after every self-contained piece.** Committing is not enough — the loss
mode here is a re-clone or a second session, and only `origin` survives those. A checkout that
is not the one you are in may be holding finished work; `git status` in each is worth ten
minutes at the start of a session. As of today only two G-Loom checkouts exist on `D:` and the
other one is at `fbccf00` with its work now recovered and pushed.

### What landed
- **Rung 3 rescued** as five blame-isolated commits: shared object filter → contract + host →
  tools + tests → registration → version. 20 tools.
- **Two defects in shipped code**, both found while verifying the plan rather than by testing:
  - `UiThread.Run` used `Task.Wait`, which wraps a faulted task in `AggregateException`, so the
    dispatcher's `ToolArgumentException` case never matched and **every** host-bound refusal
    reached the agent as *"failed: One or more errors occurred."* Now waits on the completion
    handle so `GetResult` rethrows the original type.
  - The panel stages the `.gh`/`.gloom.json` pair across its **modal** dialog, and git's index is
    repo-wide, so an MCP `gloom_commit` in that window committed the human's staged files under
    the agent's subject — and the panel's rollback then unstaged already-committed paths and said
    "nothing to commit" for a commit that happened. `Vcs/IndexGate.cs` now guards it.
- **Rung 4**: the edit envelope (`gloom_begin_edit` / `gloom_end_edit`), `gloom_set_value`,
  `gloom_restore_objects`, `Ui/DocumentRestore.cs` lifted out of the overlay, an
  `Agent editing:` row in the panel. 24 tools, 143 test cases.
- **Rung 5**: `agent/gloom/` — plugin manifest, `.mcp.json`, three skills.

### Invariants added or reinforced
- **`IndexGate` is interlocked and readable from any thread on purpose.** Checking it through
  `UiThread.Run` would wait out the full 30 s timeout during exactly the long solve it exists to
  stay clear of. Rule: **no MCP tool ever opens a modal dialog.**
- **A canvas mutation requires an open envelope.** `gloom_set_value` and `gloom_restore_objects`
  refuse without one, so there is always a version to undo from — including for edits made
  through McNeel's server, which G-Loom cannot intercept.
- **`Vcs/Identity.cs` is the only commit identity.** The panel used to hardcode a name and email
  at all three write sites while the MCP path read git config; they disagreed on any machine
  whose config differs, and this one's does.
- **Values G-Loom will not write back**: gradients and MD sliders (read by reflection over
  third-party types — writing them would mean invoking reflected methods, the thing that broke
  Grasshopper's drawing pipeline once) and internalised data (stored only as a digest). All
  three refuse out loud; `ApplyPersistent` used to silently no-op, so restoring a value list
  appeared to work and did not.

### State at end of session
- Branch `experiment/mcp` at `v0.3.0-mcp.4`, in sync with `origin`, working tree clean.
- 143 test cases green; both projects build with zero warnings.
- **Not yet smoke-tested in Rhino.** That is the next job — see the smoke-test recipe in
  `CLAUDE.md`. Note the currently deployed `GLoom.gha` predates all of this.

### Resume checklist
1. Close Rhino → `build\deploy-local.ps1` → open a definition in a G-Loom project.
2. Panel → `Agent access:` → **Read-write** → `Copy connect command` → `/mcp` shows 24 tools.
3. Walk the rung 3/4/5 smoke recipe in `CLAUDE.md`. The one that proves the whole thesis:
   `gloom_begin_edit` → watch the canvas switch to the checkpoint and highlight the agent's edit
   → `gloom_end_edit` → the drawer names the agent and intent → right-click the change to reject it.
4. Then rung 6 (the unattended dev loop) or Phase 5 work on `main`.
