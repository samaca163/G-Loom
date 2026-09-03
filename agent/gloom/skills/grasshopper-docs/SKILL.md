---
name: grasshopper-docs
description: Finding out what a Grasshopper or Rhino component does, what its inputs mean, or which component to use. Prefers the components actually installed in the running Rhino over anything on the internet.
---

# Finding out about a component

## Ask the running Rhino first

`gloom_catalogue` answers from **this** Rhino: every component installed, core and plug-in alike,
searched with Grasshopper's own fuzzy matcher — the one behind the canvas search box.

That beats the internet for three reasons: it knows which plug-ins this project's author actually
has, it returns the `componentGuid` that placement tools need, and `describe` gives the real input
and output names of the installed version rather than whatever a doc page says about some other
one. A definition written against a plug-in you assumed was absent is a common and expensive
mistake.

So:

1. `gloom_catalogue` with a `query` to find candidates.
2. `gloom_catalogue` in describe mode on the `componentGuid` for its parameters.
3. Only then reach for documentation.

## When you do need the documentation

Fetch **single pages, on demand**. Do not crawl, spider, or sweep a documentation site — its terms
forbid it, and a project that gets an IP banned helps nobody.

Useful starting points:

- `https://developer.rhino3d.com/api/grasshopper/` — the Grasshopper SDK, for plug-in work.
- `https://developer.rhino3d.com/api/rhinocommon/` — RhinoCommon, for geometry and document types.
- `https://discourse.mcneel.com/` — the McNeel forum, which is where most real answers live.

Search first, fetch the one page that looks right, stop.

## Reading a definition rather than a doc page

Often the fastest answer is the project itself. `gloom_read_document` shows every object on the
live canvas with its runtime messages and a preview of what is flowing through it, and
`gloom_read_outputs` shows the actual data on any output, branch by branch. If you want to know
what a component is doing *here*, look at what is coming out of it.
