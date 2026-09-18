# FreeCADTool — TOOL_CHECKLIST Compliance

Checked 2026-09-15 against `AIOrchestrator/API/TOOL_CHECKLIST.md` (current version incl. the
"Completion — compliance file" point). `FreeCADTool` is a pure-managed .NET agent tool that drives
an external FreeCAD instance over a local RPC bridge; it has no native or non-.NET dependencies of
its own. It sets itself up: on first use it auto-starts a headless FreeCAD + the bundled bridge, and
auto-installs the FCGear add-on on the first gear request, surfacing setup progress/failures as
localized OS notifications via the shared `AIOrchestrator.SystemNotifier`. Verified end-to-end
against a live headless FreeCAD 1.1.3 (41/41 harness checks pass, plus 10/10 field-test scenarios;
the FCGear install→Mod-dir→load→gear path verified with a live `freecadcmd` run).

## Release & layout (plugin tools)

- OK — csproj `<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>`: date auto-version.
- OK — both channels on `v*` tags: `.github/workflows/plugin-release.yml` (GitHub Release zip for hosts) + `.github/workflows/publish.yml` (NuGet `Graphene.FreeCADTool`).
- OK — AIOrchestrator referenced as sibling (`..\AIOrchestrator\AIOrchestrator.csproj` ProjectReference when present, `Graphene.AIOrchestrator` 1.* package otherwise); never copied into the plugin tree.
- OK — never ships `AIOrchestrator.dll`/dependency graph: plugin-release.yml strips the AIOrchestrator closure; the tool carries no unique native dlls (FreeCAD is external, not a packaged dependency).
- OK — the headless bridge launcher (`bridge_headless.py` + the `bridge/` package) is packed into the nupkg under `lib/net10.0/` on purpose, so the plugin ships the scripts it needs to auto-start the bridge; these are the tool's own files, not a foreign dependency, and they are plain Python (no native binaries).
- OK — the AgentBridge Chat workbench payload (`AgentBridgeChat/` → `lib/net10.0/chat_mod/`) is likewise the tool's own plain-Python/PySide files (no native binaries), packed on purpose so `InstallChatMod` can drop it into the user's FreeCAD `Mod` dir. It is not a foreign dependency and adds no native dll to the plugin.
- OK — writes no state next to the host or in `Tools/<FreeCADTool>/`: exports/saves go to the sandbox workspace, versioned via `GitSupport`; the bridge connection is runtime-only. The chat auto-install writes only into the user's own FreeCAD `Mod/AgentBridgeChat/` (the standard add-on location), never next to the host.
- OK — independent of the host launch directory: file paths resolve via `SandboxPath`/host base; the bridge endpoint comes from `FREECAD_HOST`/`FREECAD_PORT` env (with defaults), never the process CWD.

## Agent-facing descriptions

- OK — docs state outcomes, not internals (no mention of the RPC wire format, Python code generation, or the bridge transport in method docs).
- OK — minimal text; class summary is three short lines (competency + document precondition + path convention).
- OK — methods state what they do, not how.
- OK — nothing says "sandbox" or "virtual"; paths are described as relative to the "workspace root".
- OK — path-bearing methods (`export`, `import_file`, `document` open/save, `view` screenshot) use workspace-relative paths (leading `/`).
- OK — every public method has `<summary>`, `<param>`, `<returns>` covering parameters and the `Error:` format.
- OK — class summary: one-line competency + cross-method rules (active-document default, start-with-`document(create)`, path convention); it does not explain individual methods.
- OK — no summary/param redundancy; per-kind property keys live only in `<param>`.
- OK — one instruction per line; each `///` line is one continuous source line.
- OK — formats specified: lengths in mm, angles in degrees, JSON property keys and allowed enum values listed per method.
- OK — cross-references: object/sketch/feature params say they come from `list_objects` / must be created first ("sketch must exist before adding geometry", "start with document(create)").
- OK — errors are actionable (cause + detail) and prefixed `Error:`; no raw exceptions reach the agent.
- N/A — no `[[name]]` dynamic placeholders.

## Surface & sandbox

- OK — only agent operations are public (20 modeling/lifecycle methods, incl. the `create_gear` FCGear call-through); the RPC client (`FreecadBridge`) and helpers (`Run`/`Err`/`Py`/`PyJson`/`N`/`Field`) are private/internal.
- OK — every file-handling method resolves with `SandboxPath.TryResolve`/`Resolve` and converts host paths shown to the agent with `SandboxPath.ToAgent` (`export`, `import_file`, `document` open/save, `view` screenshot).
- OK — no public method escapes the sandbox or exposes credentials, configuration or host paths; results render workspace-relative only.

## Code & conventions

- OK — class name ends with `Tool`; package `Graphene.FreeCADTool`.
- OK — public methods declared directly on the class; the only inherited public member is `BaseAgentTool.LoadSkill` (the bridge is a private field; no extra public members added).
- OK — `Log.LogStep()` at the entry of every public method, plus a rich outcome log at the `Run()` RPC choke point (every modeling operation logged with success/failure).
- OK — derives from `BaseAgentTool` and implements `IFileTool` (its tasks create/modify files, so the done-without-tool guard applies).
- OK — `GenerateDocumentationFile=True` (tool definitions come from the `.xml` next to the dll).

## Standardized support — shared AIOrchestrator helpers

- N/A — the tool returns shape metrics and paths, not file-content previews: `FileManager.GetFileInfo`/`GetFilesInfo` not applicable.
- OK — every file the tool writes is versioned with `GitSupport.Snapshot` right after the write (`export`, `document` save, `view` screenshot); rollback stays centralized in `GitTool`.
- OK — every method that creates/modifies a file returns the sandbox-relative path in its result (`export`, `save`, `screenshot`), never a bare name or host path.
- N/A — the tool parses no LLM text: `Utility.RemoveFencesEncapsulationAndFixTrim` not applicable.
- N/A — no HTML/SVG output: `Utility.EmbedSvgIcons` not applicable.
- N/A — no language detection: `Utility.DetectLanguage` not applicable.
- OK — all path resolution/conversion uses `SandboxPath.Resolve`/`TryResolve`/`ToAgent`; no `Path.GetFullPath` or host paths.
- OK — OS desktop notifications use the shared `AIOrchestrator.SystemNotifier.Notify(title, body, seconds)` (Windows balloon / macOS osascript / Linux notify-send), not a hand-rolled per-OS path; the tool never duplicates the notifier.
- OK — user-facing setup strings are localized to the OS UI language (`CultureInfo.CurrentUICulture`, English fallback) via `FreeCADStrings`, matching AgentBridge's language rule; no hardcoded single-language user messages.

## Completion — compliance file

- OK — this file is shipped at the repository root of the plugin and states every checklist point above.

## Known limitations (honest notes)

- GUI-only `view` actions (screenshot, visibility, display_mode, color) require a FreeCAD GUI session; in headless mode they return a clear `Error:` rather than a wrong result. Not exercised by the headless harness.
- GUI-only `undo_redo(Undo)` / `undo_redo(Redo)`: FreeCAD's undo stack is GUI-driven, and headless `doc.undo()` is a silent no-op, so the tool raises a clear `Error:` in headless mode rather than falsely reporting a revert. `undo_redo(Status)` works headless and reports the `gui` flag. The harness asserts the GUI-required error; the GUI revert itself was confirmed manually in a GUI session (5000→1000→5000).
- `create_gear` is a call-through to the FCGear workbench (a runtime add-on, not a build dependency). The plugin bundles no FCGear code, so its GPL-3.0 license does not enter the plugin's distribution. On the first gear request, if FCGear is absent the tool installs it automatically in the background into the user's FreeCAD `Mod` dir (download + extract, one-shot, guarded by an interlocked state) and returns a localized "installing" notice; the next request builds the gear. `FREECAD_DISABLE_AUTOINSTALL=1` skips the download (used by the harness), in which case the method returns a localized notice instead. The gear-build path (Mod-dir fallback load + `setActiveDocument` + `CreateInvoluteGear.create()` + properties + recompute) was verified with a live `freecadcmd` + FCGear run on FreeCAD 1.1.3 and 0.20.2 (valid solid, volume 9798.73), and the full install→load→gear path was verified end-to-end with a live `freecadcmd` simulation of the C# installer; the harness check accepts either a valid gear or the installing notice.
- Cross-platform: the plugin is OS-neutral (`AnyCPU`, no RID) and the transport is plain TCP/JSON with no platform-specific code. Verified end-to-end (41/41 harness + 10/10 scenarios) on **Windows** (FreeCAD 1.1.3) and **Linux/WSL Debian** (FreeCAD 0.20.2), confirming the Python snippets are robust across FreeCAD versions (sketch-to-plane attachment handles both the 1.x `AttachmentSupport`/`getObject` API and the 0.20 `Support`/`OriginFeatures` API; PartDesign Revolution/Groove `ReferenceAxis` and PolarPattern `Axis` resolve to the profile-sketch axes on both). macOS uses the same OS-neutral plugin and the same FreeCAD engine; not executed here (no macOS host available), but no platform-specific code is involved.
- AgentBridge Chat panel: the C# auto-install (`EnsureChatMod`/`InstallChatMod`), the nupkg `chat_mod` packing, the Qt import shim (`_qt.py`), the headless import safety of `InitGui.py` (all three registration calls are `hasattr`-guarded), the Python syntax of the add-on, and the SSE chat-client contract were all verified (import smoke-tests pass under `freecadcmd` on FreeCAD 0.20.2 and 1.1.3; the SSE client was checked against a mock streaming server). The command's entry points use FreeCAD's own extension APIs — `Gui.addWorkbenchManipulator` for the Tools menu and the File toolbar, `FreeCAD.addDocumentObserver` for the bridge/document gate — which the bundled BIM workbench also exercises, and the discovery of `freecadcmd` is verified on real Linux (WSL: PATH, `FREECAD` not found, `/opt/freecad-1.1`, `/snap/bin`, Fedora's `FreeCADCmd`) and on Windows (a portable install on `D:` found with no environment variable). The **full GUI round-trip** — the dock actually rendering in a desktop FreeCAD, the bridge starting inside the visible GUI instance, the entry points being greyed out on the start page, and the agent editing that window live — requires a real desktop FreeCAD GUI plus a running AgentBridge and was **not** executed in this headless environment. It should be confirmed manually on a desktop before relying on the chat in production. The `commands.py` cross-process-detection gap (see MAINTENANCE.md) is handled by the in-process `_bridge_started` guard, so the document observer and the menu command cannot double-bind the ports.
