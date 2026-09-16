# FreeCADTool — TOOL_CHECKLIST Compliance

Checked 2026-09-15 against `AIOrchestrator/API/TOOL_CHECKLIST.md` (current version incl. the
"Completion — compliance file" point). `FreeCADTool` is a pure-managed .NET agent tool that drives
an external FreeCAD instance over a local RPC bridge; it has no native or non-.NET dependencies of
its own. Verified end-to-end against a live headless FreeCAD 1.1.3 (38/38 harness checks pass).

## Release & layout (plugin tools)

- OK — csproj `<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>`: date auto-version.
- OK — both channels on `v*` tags: `.github/workflows/plugin-release.yml` (GitHub Release zip for hosts) + `.github/workflows/publish.yml` (NuGet `Graphene.FreeCADTool`).
- OK — AIOrchestrator referenced as sibling (`..\AIOrchestrator\AIOrchestrator.csproj` ProjectReference when present, `Graphene.AIOrchestrator` 1.* package otherwise); never copied into the plugin tree.
- OK — never ships `AIOrchestrator.dll`/dependency graph: plugin-release.yml strips the AIOrchestrator closure; the tool carries no unique native dlls (FreeCAD is external, not a packaged dependency).
- OK — writes no state next to the host or in `Tools/<FreeCADTool>/`: exports/saves go to the sandbox workspace, versioned via `GitSupport`; the bridge connection is runtime-only.
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

- OK — only agent operations are public (19 modeling/lifecycle methods); the RPC client (`FreecadBridge`) and helpers (`Run`/`Err`/`Py`/`PyJson`/`N`/`Field`) are private/internal.
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

## Completion — compliance file

- OK — this file is shipped at the repository root of the plugin and states every checklist point above.

## Known limitations (honest notes)

- GUI-only `view` actions (screenshot, visibility, display_mode, color) require a FreeCAD GUI session; in headless mode they return a clear `Error:` rather than a wrong result. Not exercised by the headless harness.
- Cross-platform: the plugin is OS-neutral (`AnyCPU`, no RID) and the transport is plain TCP/JSON with no platform-specific code. Verified end-to-end (38/38 harness) on **Windows** (FreeCAD 1.1.3) and **Linux/WSL Debian** (FreeCAD 0.20.2), confirming the Python snippets are robust across FreeCAD versions (sketch-to-plane attachment handles both the 1.x `AttachmentSupport`/`getObject` API and the 0.20 `Support`/`OriginFeatures` API). macOS uses the same OS-neutral plugin and the same FreeCAD engine; not executed here (no macOS host available), but no platform-specific code is involved.
