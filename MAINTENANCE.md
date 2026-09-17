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

## AgentBridge Chat workbench

The plugin also ships a small FreeCAD-side add-on that puts a chat dock inside the FreeCAD GUI.
It is **not** C# — it is a Python/PySide workbench that talks to the host's OpenAI-compatible
HTTP endpoint. The C# side only *installs* it; the window lives entirely in FreeCAD.

- **Source:** `AgentBridgeChat/InitGui.py` (workbench + auto-open), `AgentBridgeChat/chat.py`
  (dock widget + SSE client), `AgentBridgeChat/_qt.py` (Qt import shim).
- **Packing:** the csproj packs `AgentBridgeChat/**` (minus `__pycache__`) to
  `lib/net10.0/chat_mod/`. `FreeCADTool.Chat.cs` → `InstallChatMod` copies that folder into the
  user's `Mod/AgentBridgeChat/` and also copies the `freecad_mcp_bridge` package next to it, so
  the workbench is self-contained and can start the bridge inside the GUI.
- **Build-output payload.** The `CopyRuntimePayloadToOutput` target (AfterTargets=Build) also
  drops `bridge_headless.py`, `bridge/` and `chat_mod/` next to the DLL in `$(OutDir)`, not just
  in the nupkg. The plugin reads these from its own assembly folder, so a locally-built plugin —
  and the dev `Tools/` deployment built from that output — must have them present or the bridge
  auto-start and the chat install silently no-op. Keep this target for any new runtime payload file.
- **Debug zero-touch (host-driven).** When AgentBridge runs under `DEBUG`, its startup sets the
  **User-level** `AGENTBRIDGE_URL` to its own chat endpoint and then calls `PreInstallChatMod`
  (via reflection on `AIOrchestrator.API.FreeCADTool`) so a manually-launched FreeCAD on the same
  machine shows the chat pointed at the debug instance with no manual env setup or file copies.
  `PreInstallChatMod` enumerates the OS-specific Mod dirs — versioned on Windows
  (`%AppData%\FreeCAD\v<major>-<minor>\Mod`), flat on Linux/macOS (`…/FreeCAD/Mod`) — and installs
  into each that has an existing FreeCAD base. The release build clears `AGENTBRIDGE_URL` so the chat
  falls back to its built-in `http://localhost:5290` default. `FREECAD_DISABLE_AUTOINSTALL=1`
  short-circuits the whole thing.
- **Same-instance driving:** `InitGui._start_bridge()` starts `FreecadMCPPlugin` in the GUI
  process on `127.0.0.1:9876` / xmlrpc `9875`, so the agent's `FreeCADTool` edits the visible
  instance. A module-level `_bridge_started` flag stops a second `_open_chat()` (auto-open + the
  manual menu command) from trying to bind the ports twice.
- **The `commands.py` gotcha.** `bridge_utils.get_running_plugin()` / `register_mcp_plugin()`
  reach for a top-level `commands` module that lives at the **RobustMCPBridge workbench root**, not
  inside the `freecad_mcp_bridge` package we copy. In a chat-Mod-only install that module is
  absent, so cross-process detection via `commands` silently no-ops. That is why the in-process
  `_bridge_started` guard is required — do not remove it. (If the full RobustMCPBridge workbench is
  *also* installed, `commands` is present and `get_running_plugin()` works as intended.)
- **Qt shim.** FreeCAD 1.x exposes Qt as `PySide` (PySide6); 0.20.x ships PySide2 whose `PySide`
  shim does **not** re-export `QtWidgets`. `_qt.py` tries `PySide → PySide2 → PySide6`. Always
  import Qt through `_qt` in this workbench, never `from PySide import …` directly.
- **Headless import safety.** `InitGui.py` guards `Gui.addCommand`/`addWorkbench` with `hasattr`
  so the module imports cleanly under `freecadcmd` (whose `FreeCADGui` is a stub without those).
  Keep that guard — it is what lets the import smoke-test run headless.
- **Verification.** Import both `chat` and `InitGui` under `freecadcmd` on **FreeCAD 0.20.2
  (WSL)** and **FreeCAD 1.1.3 (Windows)** and confirm no exception. The SSE client contract is
  verified against a mock streaming server. The full GUI round-trip (dock rendering, bridge
  starting in the GUI, agent editing the visible window) needs a real desktop FreeCAD GUI and a
  running AgentBridge — verify that manually before a release that changes the chat.
- **Overrides:** `AGENTBRIDGE_URL`, `AGENTBRIDGE_CHAT_TOOLS` (see README). `FREECAD_DISABLE_AUTOINSTALL=1`
  skips the Mod-dir write (used by the harness).

## Where the upstream lives

- Vendored reference copy: `freecad-AI/` (excluded from build and pack via the csproj
  `DefaultItemExcludes`).
- Bridge used at runtime: `freecad-AI/freecad/RobustMCPBridge/freecad_mcp_bridge` (started by
  `bridge_headless.py`).
- Our method surface: `FreeCADTool.cs`, `FreeCADTool.PartDesign.cs`, `FreeCADTool.View.cs`.
