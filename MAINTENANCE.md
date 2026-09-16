# FreeCADTool — Maintenance & Update Policy

Internal document (not shipped in the NuGet package — the csproj packs only `README.md` and
`LICENSE.md`). It tells the maintainer how to keep this tool current with its upstream,
`freecad-AI`.

## What this tool tracks

`FreeCADTool` is a C# reshaping of the upstream **`freecad-AI`** MCP server (vendored under
`freecad-AI/` in this repo for reference). The upstream is a set of Python code-generating MCP
tools that drive FreeCAD through the `RobustMCPBridge` addon. Our tool re-expresses that
capability as a small, structured C# method surface (no raw `execute_python`), scoped to the
**CAD core** (Part + PartDesign + primitives + booleans + sketch + patterns + edge ops +
import/export + view).

The upstream keeps evolving. New upstream features can be worth adopting — but only if they fit
our scope and our "few methods, functional parity" design. This document is the standing
instruction for that evaluation.

## Periodic drift check (do this every few months, or before a release)

1. **Find the upstream release date of THIS tool.** Note the date of the last `v*` tag pushed for
   `Graphene-Lab/FreeCADTool` (or the last time you synced from upstream). Call it `T_ours`.

2. **Check upstream `freecad-AI` for changes newer than `T_ours`.**
   - Inspect the vendored `freecad-AI/` tree's upstream (its GitHub repo / changelog / commit log)
     for commits, new tools, or new parameters dated **after `T_ours`**.
   - `git -C freecad-AI log --since="<T_ours>" --oneline` if the vendored copy is a git checkout;
     otherwise compare against the upstream repository's history.

3. **List candidate new capabilities.** For each upstream change after `T_ours`, note:
   - What new operation or parameter it adds.
   - Whether it is inside our CAD-core scope (Part/PartDesign/booleans/sketch/patterns/edge
     ops/import-export/view) or outside it (Draft, Spreadsheet, GUI-only extras, etc.).

4. **Decide per candidate** (prefer NOT adding — every method is a permanent surface):
   - **In scope + genuinely useful to an agent** → adopt it.
   - **Out of scope** → skip (Draft/Spreadsheet stay out; they overlap other tools).
   - **Marginal** → skip. Do not grow the surface for parity's sake.

## How to adopt a new capability (minimize method count)

The guiding rule: **add the fewest methods possible; merge into existing methods rather than
creating new ones.**

- If the new capability is a new **variant of an existing operation**, add a new enum member to
  the existing method's action enum rather than a new method.
  - Example: if upstream adds a "lofted hole" that our `feature(...)` can express, add
    `FeatureAction.LoftedHole` instead of a `CreateLoftedHole(...)` method.
- If it is a new **parameter** on an existing operation, add an optional parameter to the
  existing method.
- Create a **new method only** when the capability is a genuinely distinct operation with no
  natural home among the current methods.
- Keep the class-level `<summary>` and the method docs updated to reflect the new member/parameter,
  following the agent-facing description rules in `AGENT_TOOLS_GUIDE.md` (outcomes, not internals;
  fixed value sets as enums).

## After any change

1. Update the harness (`FreeCADTool.Harness`) with a check for the new capability; run it against
   a live headless FreeCAD on **both** Windows and Linux/WSL (the tool is verified cross-platform;
   keep it that way).
2. Re-run the full harness — it must stay fully green.
3. Update `CHECKLIST_COMPLIANCE.md` if the change touches a checklist point (e.g. new enum params,
   new file-writing method needing `SandboxPath` + `GitSupport.Snapshot`).
4. Bump the date version (the csproj auto-versions from the date) and release via the `v*` tag
   workflows when shipping.

## Cross-version note (FreeCAD 0.20 ↔ 1.x)

The tool is verified against FreeCAD 1.1.3 (Windows) and 0.20.2 (Linux/WSL). When adopting new
Python snippets from upstream, keep them robust across both FreeCAD API generations — e.g. the
sketch-to-plane attachment handles both the 1.x `AttachmentSupport`/`Origin.getObject` API and
the 0.20 `Support`/`OriginFeatures` Role-match API. Test on both before shipping.

## Where the upstream lives

- Vendored reference copy: `freecad-AI/` (excluded from build and pack via the csproj
  `DefaultItemExcludes`).
- Bridge used at runtime: `freecad-AI/freecad/RobustMCPBridge/freecad_mcp_bridge` (started by
  `bridge_headless.py`).
- Our method surface: `FreeCADTool.cs`, `FreeCADTool.PartDesign.cs`, `FreeCADTool.View.cs`.
