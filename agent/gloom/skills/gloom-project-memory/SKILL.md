---
name: gloom-project-memory
description: How to work on a Grasshopper project through G-Loom - reading its record of decisions, seeing the live canvas, and making changes that land as reviewable, revertible versions. Use whenever the work involves a .gh definition, a G-Loom project, or the gloom_* tools.
---

# Working on a Grasshopper project through G-Loom

G-Loom versions the **recipe** — the Grasshopper graph that produces the geometry — not the
geometry. So a project's history is a chain of design decisions, and every version can be read,
compared and returned to. Your job is to leave that history better than you found it.

## Start by finding out where you are

Call `gloom_status` first, every time. It tells you which definition is active, its project root,
which version it stands at, and whether there are unsaved edits on the canvas. Almost every other
tool defaults to that definition, so getting this wrong quietly aims your work at the wrong file.

Then, depending on what you were asked:

- **"What is this?" / "Why is it like this?"** → `gloom_decision_record`, or the
  `gloom://definition/<path>/record` resource. This is the project's reasoning, not just a log.
- **"What changed?"** → `gloom_diff` for the facts, `gloom_explain_changes` for the narrative.
  Both default to comparing the last committed version against the file on disk.
- **"What is on the canvas right now?"** → `gloom_read_document`. This is the *live* graph,
  including unsaved edits and each component's runtime errors and warnings — none of which the
  committed recipe holds. `gloom_read_outputs` gets the actual data on any output.
- **"Is something broken?"** → `gloom_read_document` lists failing objects with their messages.
  `gloom_solve` recomputes and reports what failed.
- **"Show me"** → `gloom_canvas_image`.

## Before you change anything: open an envelope

**Any change to the canvas starts with `gloom_begin_edit` and a stated intent.** Not because it is
ceremony, but because of what it does:

1. It **checkpoints** the definition, committing the human's unsaved work first, so there is always
   a version to go back to — including from changes you make through *other* tools.
2. It **aims G-Loom's canvas overlay at that checkpoint**, so the person sitting in front of Rhino
   watches your changes light up on their canvas as you make them, and can reject any one of them
   by right-clicking it. You are being reviewed in real time. That is the point.

`gloom_set_value` refuses while no envelope is open.

When you are done, `gloom_end_edit` with a subject and a description. The commit is attributed to
the human — it is their project — and names you in its trailers, so the history says who did what
and why. If it went badly, `gloom_end_edit` with `discard: true` puts everything back.

## What you may change, and what you may not

**G-Loom sets values**: sliders, panels, toggles, value lists, colour swatches — via
`gloom_set_value`, in batches, with before-and-after reported so you can say in the commit what
actually moved.

**G-Loom does not author graphs.** Adding, removing, connecting or rewiring components is Rhino's
own MCP server's job. If both are connected, use that one for structure and G-Loom for values,
history and review. Do it inside a G-Loom envelope anyway: the checkpoint covers whatever changed
the canvas, no matter which tool did it.

**Never** push, delete a branch, or delete a tag. G-Loom gives you no tool for those on purpose.

## Undoing things, in order of increasing violence

1. `gloom_restore_objects` — put specific objects back as a version had them, leaving the rest
   alone. This is the surgical one, and usually the right one.
2. `gloom_end_edit` with `discard: true` — throw away everything since the checkpoint.
3. `gloom_revert` — put the whole definition back to a version. Destructive: uncommitted edits go.

## Say "system option", not "branch"

A branch in a G-Loom project is a **substitutable design strategy** — `envelope-mullion` versus
`envelope-unitized` — not a place where work happened. `gloom_branches` lists them; talk about them
as options the project is choosing between, because that is what they are to the person you are
working for. Same for tags: they are **milestones**, and each pins the exact Rhino, Grasshopper and
Rhino.Inside.Revit versions it was made on, so the deliverable stays reproducible years later
(`gloom_toolchain`).

## Writing a version

The subject is one line naming the **design decision**, not the mechanics. "Raise the podium to
four storeys" — not "changed slider from 3 to 4". The description is why. Someone will read this in
two years while trying to understand a building.
