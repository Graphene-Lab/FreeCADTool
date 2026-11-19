# FreeCADTool — notes for coding agents

> Read this file first. This repo is one tool plugin of the AIOffice solution: it is compiled on
> its own and loaded by the hosts from `Tools/FreeCADTool/` at runtime.

## What this is

Parametric CAD through a running FreeCAD instance, over a local RPC bridge: primitives,
PartDesign bodies, sketches, pad/pocket/revolve, patterns, fillet/chamfer, booleans, document
lifecycle, STEP/STL/3MF/OBJ/IGES import-export, views and macros.

## Read before you edit

| If you touch… | Read first |
|---|---|
| anything | [PLUGIN-GUARDRAILS](../AIOrchestrator/docs-dev/PLUGIN-GUARDRAILS.md) — the contract shared by the 12 plugins; the file lives in the sibling AIOrchestrator repo (private), reachable from the solution checkout |
| the bridge or the auto-start of FreeCAD | `bridge/`, `bridge_headless.py` and `MAINTENANCE.md` |
| the improvement process | [IMPROVEMENT-METHODOLOGY.md](IMPROVEMENT-METHODOLOGY.md), [IMPROVEMENT_LOG.md](IMPROVEMENT_LOG.md) |
| a local divergence from upstream | [LOCAL-FORK-NOTES.md](LOCAL-FORK-NOTES.md) |
| a tool method or its XML docs | [AGENT_TOOLS_GUIDE](../AIOrchestrator/API/AGENT_TOOLS_GUIDE.md) |

## Non-negotiables

1. The class derives from `BaseAgentTool` and keeps `[ToolPurpose(ToolPurpose.Cad)]`.
2. Public methods and their XML docs **are** the agent's surface: no public method without docs,
   and a public method is never renamed (the model calls it by that name).
3. AnyCPU, no `RuntimeIdentifier`, `GenerateDocumentationFile` stays on.
4. The plugin folder never carries `AIOrchestrator.dll`; no state is written into `Tools/`.
5. **The Python payload is part of the plugin.** `bridge_headless.py`, `bridge/` and
   `AgentBridgeChat/` are packed into the nupkg and copied next to the dll by the csproj; the
   plugin finds them through its own assembly location. Moving or renaming them breaks the
   headless auto-start and the FreeCAD chat command.
6. `freecad-AI/` is a vendored reference tree: it is excluded from compile and pack, and must stay
   that way.
7. A new metadata goes in a class attribute (or a `virtual` member with a default) — never an
   `abstract` member: the dlls already shipped must keep loading.

## Where things are

- `FreeCADTool.cs` — the tool class; its public methods are what the model can call
- `bridge/`, `bridge_headless.py` — the RPC bridge and the headless FreeCAD launcher
- `AgentBridgeChat/` — the FreeCAD workbench payload (installed into the user's Mod folder)
- `installer/` — the FreeCAD+AgentBridge installer
- `FreeCADTool.Harness/`, `FreeCADTool.Scenarios/`, `FreeCADTool.ProductionSim/` — nested projects,
  excluded from the library compile

## Shipping

Publishes a GitHub release zip **and** a NuGet package; AgentBridge ships it in every release.
AIOffice does not build it into its dev `Tools/`.
