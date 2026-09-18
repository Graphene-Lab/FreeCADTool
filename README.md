# FreeCADTool — Parametric CAD for AI Agents

![FreeCADTool orbiting a parametric turbine impeller built and rendered through a live FreeCAD session](freecad_demo.gif)

**FreeCADTool** is a pure C# / .NET agent plugin that lets an AI agent create and edit real
parametric 3D CAD models inside [FreeCAD](https://www.freecad.org): primitives, PartDesign
bodies with sketches, pad / pocket / revolve / groove / hole / loft / sweep, linear / polar /
mirror patterns, fillet / chamfer, boolean fuse / cut / common, and import / export of the
standard CAD formats (STEP, STL, 3MF, OBJ, IGES). It is the C# counterpart of the
`freecad-AI` MCP server, reshaped into one structured, deterministic agent tool.

The animation above is a turbine impeller — a 12-blade rotor with a revolved hub, top cap,
mounting flange, outer shroud, central bore and a bolt circle — built as a single solid and
rendered by orbiting the FreeCAD camera. Every frame is a real FreeCAD render, not a mockup.

## What it is (and is not)

FreeCADTool does **not** contain a CAD kernel. It drives a running **FreeCAD** instance over a
local RPC bridge (the same bridge `freecad-AI` uses). FreeCAD is an external engine — not a
compile-time dependency and not a NuGet wrapper. The plugin itself is pure, managed .NET with
**no native or non-.NET dependencies of its own**, so it loads cleanly into any
[AIOrchestrator](https://github.com/Graphene-Lab) host (AgentBridge, AIOffice).

| | |
|---|---|
| **Language** | C# / .NET 10 (`AnyCPU`, OS-neutral) |
| **Engine** | FreeCAD 1.x and 0.20.x (Part + PartDesign + Sketcher) |
| **Transport** | JSON-RPC over TCP `127.0.0.1:9876` |
| **Platforms** | Windows · Linux · macOS |
| **Scope** | CAD core: primitives, bodies, sketches, features, patterns, edge ops, booleans, import/export, view |
| **License** | AGPL-3.0 |

## How it works

```
FreeCADTool (C#)  ──JSON-RPC──▶  FreeCAD + bridge (TCP 127.0.0.1:9876)
                                       │
                                       └─ FreeCAD's own Part / PartDesign / Sketcher engines
```

The plugin sends a small, purpose-built Python snippet per operation to the bridge and reads back
a structured result. The agent never sees or writes Python — it calls typed methods and gets
plain-text results (or a deterministic `Error: …` string).

### What you need to install

You install three things. The tool does everything else by itself — you never start FreeCAD,
start a bridge, or install an add-on by hand.

1. **FreeCAD** — the CAD engine the tool drives. Install 1.1.x (recommended) or 0.20.x from
   <https://www.freecad.org/downloads>. On Linux: `sudo apt install freecad`.
2. **AgentBridge** (or AIOffice) — the host that runs the agent. FreeCADTool is a plugin for it
   and cannot run on its own.
3. **FreeCADTool** — this plugin. Install it through the host's plugin manager, or unpack the
   release zip into the host's `Tools/FreeCADTool/` folder.

### What the tool sets up automatically

- **The bridge starts itself.** The first time the agent uses FreeCADTool and nothing is listening
  on the bridge port, the tool finds your FreeCAD install and launches a **headless FreeCAD**
  with the bundled bridge in the background. The first call may pause a few seconds while it comes
  up; later calls reuse it. The bridge launcher ships inside the plugin, so there is nothing to
  copy or configure.
- **FCGear installs itself.** The first time the agent asks for a real gear, the tool downloads
  the [FCGear](https://github.com/looooo/freecad.gears) add-on into your FreeCAD `Mod` folder in
  the background and uses it — no restart, no Addon Manager. You get a desktop notification when
  it is ready.
- **A chat panel installs itself inside FreeCAD.** The first time the tool runs, it also drops
  **AgentBridge Chat** into your FreeCAD `Mod` folder. It appears in FreeCAD's own **Tools** menu
  and as a button in the **File** toolbar next to New/Open/Save, in every workbench, and it is
  enabled while a document is open — the same rule FreeCAD applies to every command that acts on a
  drawing (see [Chat inside FreeCAD](#chat-inside-freecad)).
- **You are told what is happening, in your language.** Setup progress and any problem appear as
  a normal desktop notification, in your operating system's language (English, Italian, French,
  Spanish, German, Russian; English for any other language).

### Why the auto-started FreeCAD is headless (and how to watch the work)

When the tool starts FreeCAD for you, it starts a **headless** one — a FreeCAD with no window.
This is deliberate, not a limitation.

The tool's host, **AgentBridge, is a bridge**: the agent can be driven over its API by a client on
a *different machine*, or run on a **server with no desktop environment** at all. A windowless
FreeCAD is the only thing that works in every one of those cases — there may be no screen to draw
on. So the automatic path never assumes a GUI.

If you **want to see the agent work**, open both apps yourself on the same machine:

1. Start **AgentBridge**.
2. Open the **FreeCAD GUI** and create or open a document: the bridge starts with the first
   document, inside that visible window, so the agent edits the geometry you see on screen.
   Opening the chat panel (**Tools → Open AgentBridge Chat**, or the button in the File toolbar)
   starts the same bridge if it is not up yet (see
   [Chat inside FreeCAD](#chat-inside-freecad)).

Driving the agent from AgentBridge while FreeCAD is closed is the headless case: the work is real
and saved to a file, but there is no window to show it in. To get a GUI bridge instead of the
headless one, start the bridge yourself with `freecad startup_bridge.py` before the agent's first
call (see [Starting the bridge yourself](#starting-the-bridge-yourself-optional)).

### If FreeCAD is not found

If the tool cannot find FreeCAD, a notification asks you to install it. You can also point the
tool at a specific install with environment variables (advanced — most users never need these):

| Variable | Meaning |
|---|---|
| `FREECAD_CMD` | Full path to the `freecadcmd` executable. Used as-is. |
| `FREECAD_HOME` | FreeCAD install folder. The tool looks for `freecadcmd` in it and in its `bin/`. |
| `FREECAD_HOST` | Bridge host (default `127.0.0.1`). |
| `FREECAD_PORT` | Bridge port (default `9876`). |
| `FREECAD_DISABLE_AUTOINSTALL=1` | Never auto-download FCGear (for locked-down or offline setups). |

With none of these set the tool searches for itself, on every OS and whatever the install path:

- **Windows** — `freecadcmd.exe` on the `PATH`; `Program Files`, `Program Files (x86)`,
  `%LOCALAPPDATA%\Programs` and the root of every fixed drive, under `FreeCAD*\bin\` or `FreeCAD*\`
  (the portable archives are unpacked anywhere, which is why whole drives are checked).
- **Linux** — `freecadcmd` on the `PATH` (the Debian/Ubuntu name), `/usr/bin/freecadcmd`,
  `/usr/bin/FreeCADCmd` (Fedora), `/usr/local/bin`, `/snap/bin`, and `FreeCAD*` / `freecad*` under
  `/opt` and `/usr/lib` (the unpacked archive and the Ubuntu PPA).
- **macOS** — `freecadcmd` on the `PATH`, and `FreeCAD*.app` in `~/Applications` and
  `/Applications`.

A Flatpak or Snap **sandbox** cannot be driven from outside it (the bridge has to run inside the
sandbox, and the plugin cannot start it there): point `FREECAD_CMD` at a `freecadcmd` that runs
outside it, or install FreeCAD another way. Whatever the tool finds is written to its log
(`FreeCADBootstrap: freecadcmd found at …`), so a support question can start from the path it
actually used.

### Starting the bridge yourself (optional)

You can start the bridge yourself instead of letting the tool do it — useful when you want the
GUI (for screenshots and view control) or a specific FreeCAD build:

- **Headless:** `freecadcmd bridge_headless.py` — full modeling surface, no GUI.
- **GUI:** `freecad startup_bridge.py` — the bridge inside the FreeCAD GUI, so `view` works too.

The tool connects to whatever is already listening on the port, so a bridge you started yourself
is used instead of launching a new one.

## Chat inside FreeCAD

Alongside the agent methods, the plugin installs a small **chat add-on** that puts a chat panel
directly inside the FreeCAD window. You type a prompt in FreeCAD, the local **AgentBridge** agent
runs it, and the result — including any CAD edits — shows up in the very FreeCAD you are looking
at.

```
FreeCAD GUI + AgentBridge Chat dock  ──HTTP (SSE)──▶  AgentBridge  ──agent──▶  FreeCADTool
        ▲                                                   │                    │
        └──────────── the same FreeCAD instance ◀── MCP bridge started in the GUI ┘
```

- **Same-instance driving.** The bridge starts *inside the visible GUI FreeCAD* (not a separate
  headless one) — when the first document opens, or when you open the chat — so when the agent
  calls `FreeCADTool`, the geometry you see on screen is what changes. If a bridge is already
  running in that instance, the panel reuses it instead of starting a second one.
- **Where it lives, and when it is available.** **Tools → Open AgentBridge Chat**, and a button in
  the **File** toolbar next to New/Open/Save, in every workbench (added through FreeCAD's
  workbench-manipulator API — the same mechanism the bundled BIM workbench uses). Both are
  **disabled while no document is open**, exactly like FreeCAD's own drawing commands: a chat that
  asks the agent to change a project means nothing before there is a project. Nothing opens by
  itself at startup.
- **Reopen any time.** If you close the dock, open it again from the same menu entry or button:
  the conversation, and the AgentBridge session behind it, are kept.
- **All OSes.** Qt is imported through a small shim that works on FreeCAD 1.x (PySide6) and 0.20.x
  (PySide2) alike, on Windows, Linux and macOS.
- **What the agent can do from the chat.** By default the chat runs the agent with `FileTool`,
  `GitTool` and `FreeCADTool`, so it can read/write files, manage git, and model CAD. The agent
  streams its reply token-by-token over Server-Sent Events, and the conversation keeps its context
  across turns via the AgentBridge `session_id`.
- **Requirements.** AgentBridge (or any host exposing the same OpenAI-compatible endpoint) must
  be running on the machine. The chat talks to it over `http://localhost:5290/v1/chat/completions`
  — or `:5291`, the port a **debug build** started from the IDE serves (the host shifts its
  defaults in Debug). The panel probes both and uses the one that answers, so no configuration is
  needed either way. If AgentBridge is not running at all, opening the chat **starts it**
  (see *Reverse start* below).
- **Reverse start.** The installer records the AgentBridge executable and endpoint next to the
  panel (`agentbridge.json`). Opening the chat starts AgentBridge itself, in the background, and
  waits for it — so the whole loop can be driven from FreeCAD alone, without launching the host
  first. Turn it off by editing `autostart` in that file.
- **Overridable** with environment variables (advanced):

  | Variable | Meaning |
  |---|---|
  | `AGENTBRIDGE_URL` | Full chat endpoint (default `http://localhost:5290/v1/chat/completions`). |
  | `AGENTBRIDGE_CHAT_TOOLS` | Comma-separated tool names for the chat agent (default `FileTool,GitTool,FreeCADTool`). |

> **Ordering note.** For the agent to edit the window you see, the FreeCAD GUI (and its chat
> bridge) must be up before the agent's first `FreeCADTool` call. This is the natural case when you
> are chatting from inside FreeCAD. If the agent started a *headless* FreeCAD first (it grabs the
> bridge port), the GUI bridge cannot bind the same port — close the headless instance or point the
> chat at a different `FREECAD_PORT` if you hit this.

## Agent methods

Fixed value sets (the `action` / `operation` / `kind` parameters) are **C# enums**, not free
strings — the agent is shown the exact allowed values and any value outside the set is rejected
before the method runs.

| Agent method | Purpose |
|---|---|
| `status()` | Report the FreeCAD connection, version, and whether the GUI is available. Call first. |
| `document(action, name, path)` | `List` \| `Create` \| `Open` \| `Save` \| `Close` \| `Recompute`. |
| `list_objects(docName)` | List objects (name, label, type). Source of names for every other method. |
| `inspect_object(name, docName)` | One object's properties and shape metrics (volume, area, validity, counts). |
| `delete_object(name, docName)` | Remove an object (undoable). |
| `create_primitive(kind, properties, name, docName)` | `Box` \| `Cylinder` \| `Sphere` \| `Cone` \| `Torus` \| `Wedge` \| `Helix`. |
| `create_object(typeId, name, properties, docName)` | Any FreeCAD object type by id. |
| `edit_object(name, properties, docName)` | Set properties on an existing object. |
| `transform(name, position, rotation, scale, docName)` | Move, rotate and/or scale an object. |
| `boolean(operation, objectNames, name, docName)` | `Fuse` \| `Cut` \| `Common` of solids. |
| `export(format, path, objectNames, docName)` | `step` \| `stl` \| `3mf` \| `obj` \| `iges`. The file is versioned after writing. |
| `import_file(path, docName)` | Import a CAD file into the document. |
| `get_complex_part(name)` | Fetch a ready-made part from the public FreeCAD Parts Library and import it (`.FCStd` first, else STEP/IGES/STL). `name` is in English. |
| `undo_redo(action, docName)` | `Undo` \| `Redo` \| `Status`. `Undo`/`Redo` need the GUI (no-op in headless); `Status` works anywhere. |
| `create_body(name, docName)` | A PartDesign Body — the container for feature-based modeling. |
| `sketch(action, sketchName, body, plane, properties, docName)` | `Create` \| `Rectangle` \| `Circle` \| `Line` \| `Arc` \| `Point`. |
| `feature(action, sketch, body, properties, docName)` | `Pad` \| `Pocket` \| `Revolve` \| `Groove` \| `Hole` \| `Loft` \| `Sweep` from a sketch. |
| `pattern(action, feature, body, properties, docName)` | `Linear` \| `Polar` \| `Mirror` pattern of a feature. |
| `edge_op(operation, baseObject, edges, size, docName)` | `Fillet` \| `Chamfer` edges of a solid. |
| `create_gear(properties, name, docName)` | Parametric involute gear (spur/helical) via the FCGear workbench — the add-on installs itself on first use. |
| `view(action, path, properties, docName)` | `Screenshot` \| `Angle` \| `Fit` \| `Zoom` \| `Visibility` \| `DisplayMode` \| `Color` (needs the GUI). |

Typical agent flow:

```
status()
→ document(Create, "part")
→ create_body("b")
→ sketch(Create, "sk", "b", "XY_Plane")
→ sketch(Rectangle, "sk", properties={"x":0,"y":0,"width":20,"height":10})
→ feature(Pad, "sk", "b", {"length": 10})
→ export("step", "/out/part.step")
```

## Ready-made parts (without drawing them)

**Complex real-world objects are searched for, not modeled.** For anything primitives cannot
reproduce — an astronaut, a spaceship, a robot arm, a gearbox, a machine tool — the agent must call
**`get_complex_part(name)` first**, and only fall back to modeling when the search finds nothing.
The method is documented that way in the tool itself, so the agent follows the rule without being
told in the prompt.

`get_complex_part(name)` searches the public **[FreeCAD Parts Library](https://github.com/FreeCAD/FreeCAD-library)**
(the community catalog the FreeCAD Addon Manager indexes: several thousand contributed parts across
`Robots/`, `Parts/`, `Electrical Parts/`, `Fasteners/`, `Architectural Parts/` …), downloads the
best match into the workspace and imports it into the active document.

- **`name` is in English** — plain catalog words, one or two is best (`"spur gear"`, `"ball
  bearing"`, `"robot arm"`), not a path or a file name. The agent translates first (`astronave` →
  `spaceship`).
- **The best match wins.** Paths whose name matches the query best come first, and inside the same
  match quality the **most complete document** is taken — the library publishes no rating, so the
  larger file (more features, more detail) is the deterministic stand-in, exactly as the ranking
  comment in the source describes. Plural and short variants of a word count as a match (`robot` /
  `robots`, `gear` / `gears`).
- **`.FCStd` first** — a native document keeps its parametric feature tree, so the imported part
  stays editable; STEP/IGES/STL are imported as an unparametric shape only when no `.FCStd` matched
  (the result says so).
- **It never clones the 5.8 GB repository.** The file list comes from the GitHub tree API, the index
  is cached in the workspace (`.freecad-library-index.json`, one week) and only the single winning
  file is downloaded. Anonymous GitHub API rate limit is 60 requests/hour — the error names it when
  it is hit.
- **Alternatives are printed.** The result lists the next candidates by match quality, so the agent
  can call again with a neighbouring name when the geometry is not the one it wanted.

You can also bring in a part you found yourself: download any standard part (McMaster-Carr,
Traceparts, GrabCAD, …) as **STEP / IGES / STL / OBJ** and use `import_file(path)`. It lands in the
active document as an editable object you can then `transform`, `boolean`, `export`, or use as a
reference.

Parametric standard parts the tool can generate itself — bolt circles, washers, spacers, flanges,
and a simple toothed wheel (a base cylinder plus a `Polar` pattern of teeth) — are built with the
existing primitives + patterns + booleans, so they stay version-independent and need nothing
installed.

**Gears: `create_gear` via FCGear.** For real involute / helical gears the tool offers one
workbench call-through: `create_gear(properties)` drives the
[FCGear](https://github.com/looooo/freecad.gears) workbench (`CreateInvoluteGear`) with
`teeth`, `module`, `height`, `pressure_angle`, `helix_angle`, `shift`, `axle_hole`, etc. This is
a *call-through*, not a bundle: the plugin ships **zero** FCGear code — it sends a snippet that
calls the workbench already installed in the user's FreeCAD, so FCGear's GPL-3.0 license never
enters the plugin's distribution. FCGear's headless path is proven by its own CI, and the gear
build is verified here on FreeCAD 1.1.x and 0.20.x. If the add-on is not yet installed, the tool
installs it automatically in the background the first time you ask for a gear (see *What the tool
sets up automatically*) and notifies you when it is ready; the next gear request then builds
normally. Set `FREECAD_DISABLE_AUTOINSTALL=1` to stop the tool from downloading it — in that case
the method returns a clear `Error:` and you can install FCGear yourself from the FreeCAD Addon
Manager, or fall back to the primitive + `Polar` pattern above.

**Why the other part workbenches are not wrapped.** The Fasteners workbench
(`FreeCAD_FastenersWB`) and BOLTS / BOLTSFC are excellent but a poor fit as core methods: the
Fasteners headless path is fragile (a top-level `import FreeCADGui`) and its scripted API is
awkward, and BOLTS is GUI-oriented and largely superseded — neither is dependable under
`freecadcmd`. CadQuery is a separate Python CAD library with no free C#/.NET package, so it is
out of scope for a pure-.NET tool. The supported pattern stays: **import the part file, build it
parametrically with the methods above, or use `create_gear` for gears.**

## Cross-platform

The plugin is OS-neutral (`AnyCPU`, no runtime identifier) and runs wherever .NET runs. The
FreeCAD engine it drives is available on Windows, macOS and Linux, so the whole tool works on all
three. The transport is plain TCP and JSON, with no OS-specific code. The full method surface is
verified against FreeCAD 1.1.x (Windows) and 0.20.x (Linux) in the test harness.

## GUI-only features

`view` screenshot, visibility, display mode and color need a FreeCAD GUI session. So do
`undo_redo(Undo)` and `undo_redo(Redo)`: FreeCAD's undo stack is driven by the GUI, and in
headless mode `doc.undo()` is a silent no-op, so the tool raises a clear `Error:` rather than
pretending to revert. `undo_redo(Status)` works anywhere and reports whether the GUI is up.
Everything else — modeling, booleans, patterns, edge operations, import/export — works fully
headless.

## What was reshaped from `freecad-AI`

- The many code-generating MCP tools are merged into a small, structured method surface: no raw
  `execute_python` is exposed to the agent. Each method maps to a concrete CAD operation.
- Fixed value sets are C# enums, so the allowed values are part of each method's signature and
  the generated tool scheme.
- Adapted to the agent-tool contract: `BaseAgentTool`, namespace `AIOrchestrator.API`,
  agent-oriented XML docs, `Log.LogStep` tracing, sandbox path handling, version-before-write.
- Deterministic `Error: cause — detail` strings on every failure path instead of raw exceptions.
- Scope is the CAD core. Draft and Spreadsheet are intentionally out of scope.

## Troubleshooting

| Symptom | What it means / what to do |
|---|---|
| Notification: "FreeCAD … was not found" | FreeCAD is not installed, or not where the tool looks. Install it from <https://www.freecad.org/downloads>, or set `FREECAD_CMD` / `FREECAD_HOME` to your install. |
| Notification: "Could not start FreeCAD automatically" | FreeCAD is installed but the headless bridge did not come up. Open FreeCAD once (to finish first-run setup), or start the bridge yourself with `freecadcmd bridge_headless.py`. |
| First `status()` / call is slow | Normal — the tool is starting a headless FreeCAD in the background. Later calls are fast. |
| `create_gear` says it is "installing FCGear" | Normal on the first gear request. Wait for the "FCGear installed" notification, then ask again. |
| `undo` / `redo` returns a GUI error | Expected in headless mode — FreeCAD's undo stack needs the GUI. Run the bridge with `freecad startup_bridge.py` if you need undo/redo. |
| `view` screenshot fails | `view` needs the GUI. Start the bridge with `freecad startup_bridge.py`. |
| Port already in use / wrong instance | Point the tool elsewhere with `FREECAD_HOST` / `FREECAD_PORT`, or stop the other bridge. |

## Development

```bash
dotnet build FreeCADTool.csproj
# start a headless FreeCAD bridge (needs FreeCAD installed), then:
dotnet run --project FreeCADTool.Harness   # end-to-end checks against the live bridge
```

The harness runs the full CAD-core surface (primitives, booleans, sketch + pad/pocket/revolve,
patterns, fillet/chamfer, transform, edit, undo/redo, export/import) and prints one line per
check, ending with a pass/fail total.

## Release

Push a tag `v*` (date version, e.g. `v1.26.09.15`) — the two workflows publish both channels:

- `plugin-release.yml` → GitHub Release with the self-contained `FreeCADTool-<version>.zip`,
  which the hosts (AgentBridge, AIOffice) install into `Tools/FreeCADTool/`.
- `publish.yml` → NuGet package `Graphene.FreeCADTool` (the repository needs the
  `NUGET_API_KEY` secret — same key as the other Graphene-Lab repos).

A plain push is a code-only sync and publishes nothing. The repository must stay **public**
(the hosts download the release zip anonymously).

## License

[GNU Affero General Public License v3.0](LICENSE.md)
