using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIOrchestrator;

namespace AIOrchestrator.API;

/// <summary>FreeCAD parametric CAD for agent use: create and edit 3D primitives and PartDesign bodies, sketch profiles, pad/pocket/revolve/groove, linear/polar/mirror patterns, fillet/chamfer, boolean fuse/cut/common, and import/export STEP/STL/3MF/OBJ/IGES.
/// Requires a running FreeCAD instance. Operations act on the active document unless a document name is given — start with document("create", name) or document("open", path).
/// File paths are Unix-style relative to the workspace root (leading "/", e.g. /part.FCStd, /out/model.step). Saved/exported files are versioned; roll back via GitTool.restore.</summary>
public partial class FreeCADTool : BaseAgentTool, IFileTool
{
    private readonly FreecadBridge _bridge = new();

    // Cached once the bridge is confirmed reachable, so we don't TCP-probe on every call.
    private static bool _bridgeReady;

    /// <summary>Send Python to FreeCAD and return the raw result. Ensures a headless
    /// FreeCAD + bridge is running first, starting one automatically if needed.</summary>
    private ExecResult Run(string code, int? timeoutMs = null)
    {
        var boot = EnsureBridge();
        if (boot != null) return boot.Value;
        var r = _bridge.Execute(code, timeoutMs);
        if (!r.Success && r.ErrorType == "ConnectionError") _bridgeReady = false;
        Log.LogStep($"FreeCADTool.run: {(r.Success ? "ok" : "FAILED — " + (r.ErrorType ?? r.Stderr ?? "error"))}");
        return r;
    }

    /// <summary>Ensure the FreeCAD bridge is reachable, auto-starting a headless
    /// FreeCAD if it is not. Returns null when ready, or a failed <see cref="ExecResult"/>
    /// carrying a localized message (and shows a desktop notification) when setup fails.</summary>
    private ExecResult? EnsureBridge()
    {
        if (_bridgeReady) return null;
        if (FreeCADBootstrap.IsBridgeUp(_bridge.Host, _bridge.Port)) { _bridgeReady = true; EnsureChatMod(); return null; }

        // Nothing is listening — start FreeCAD ourselves. Tell the user why the first
        // call may pause (no-op on headless systems with no desktop notifier).
        SystemNotifier.Notify("FreeCADTool", FreeCADStrings.Body("BridgeStarting"));
        var reason = FreeCADBootstrap.EnsureBridge(_bridge.Host, _bridge.Port);
        if (reason == null) { _bridgeReady = true; EnsureChatMod(); return null; }

        var msg = reason == "freecad_not_found"
            ? FreeCADStrings.Body("FreecadNotFound")
            : FreeCADStrings.Body("BridgeFailed");
        SystemNotifier.Notify("FreeCADTool", msg);
        return new ExecResult(false, null, "", msg, "BootstrapError", null);
    }

    /// <summary>Build a failure message from a failed ExecResult, or null when it succeeded.</summary>
    private static string? Err(ExecResult r, string action)
    {
        if (r.Success) return null;
        var detail = !string.IsNullOrWhiteSpace(r.ErrorTraceback) ? r.ErrorTraceback
                   : !string.IsNullOrWhiteSpace(r.Stderr) ? r.Stderr
                   : r.ErrorType ?? "unknown error";
        return $"Error: {action} failed — {detail}";
    }

    /// <summary>C# string → Python string literal (None for null).</summary>
    private static string Py(string? s)
    {
        if (s is null) return "None";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>JSON object string → Python expression yielding a dict (empty dict when blank).</summary>
    private static string PyJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        return "json.loads(" + Py(json) + ")";
    }

    /// <summary>Format a double for Python (invariant).</summary>
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Read a string field from an ExecResult's result element.</summary>
    private static string? Field(ExecResult r, string prop)
        => r.Result.HasValue && r.Result.Value.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

    // ─────────────────────────────────────────────────────────────────────
    // Connection / status
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Report the FreeCAD connection: whether the bridge is reachable, the FreeCAD version, Python version, and whether the GUI is available (GUI-only features like screenshots need it). Call first to confirm the tool can reach FreeCAD.</summary>
    /// <returns>Status text: connected/version/gui, or "Error:" with the connection problem.</returns>
    public string Status()
    {
        Log.LogStep("FreeCADTool.Status");
        var ping = _bridge.Ping();
        if (ping < 0)
            return $"Error: cannot reach the FreeCAD bridge at {_bridge.Host}:{_bridge.Port}. Start FreeCAD and the MCP bridge, then retry.";
        var r = Run("import FreeCAD, sys\n_result_ = {'version': '.'.join(FreeCAD.Version()[:3]), 'gui': bool(FreeCAD.GuiUp), 'py': sys.version.split()[0]}");
        if (!r.Success) return Err(r, "status")!;
        return $"Connected to FreeCAD {_bridge.Host}:{_bridge.Port} (ping {ping:F0} ms) — version {Field(r, "version")}, Python {Field(r, "py")}, GUI {(Field(r, "gui") == "True" ? "available" : "headless")}.";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Document lifecycle
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Manage FreeCAD documents. List (all open docs), Create (new doc, needs name), Open (needs path), Save (active or named doc; optional path to save-as), Close (named or active doc), Recompute (rebuild all objects). Most modeling methods act on the active document, so create/open one first.</summary>
    /// <param name="action">List | Create | Open | Save | Close | Recompute.</param>
    /// <param name="name">Document name (no spaces). Required for Create; used to target Close.</param>
    /// <param name="path">Workspace path for Open (e.g. /model.FCStd) or save-as target for Save.</param>
    /// <returns>Result text. Open/Save return the workspace path; List returns the documents; errors prefixed "Error:".</returns>
    public string Document(DocumentAction action, string? name = null, string? path = null)
    {
        Log.LogStep($"FreeCADTool.Document: action={action}");
        switch (action)
        {
            case DocumentAction.List:
            {
                var r = Run("_result_ = [{'name': d.Name, 'label': d.Label, 'objects': len(d.Objects)} for d in FreeCAD.listDocuments().values()]");
                if (!r.Success) return Err(r, "list documents")!;
                var items = r.Result.HasValue ? JsonSerializer.Serialize(r.Result.Value) : "[]";
                return $"Open documents: {items}";
            }
            case DocumentAction.Create:
            {
                if (string.IsNullOrWhiteSpace(name)) return "Error: 'create' needs a document name.";
                var r = Run($"doc = FreeCAD.newDocument({Py(name)})\n_result_ = {{'name': doc.Name, 'label': doc.Label}}");
                if (!r.Success) return Err(r, "create document")!;
                return $"Created document '{Field(r, "name")}'.";
            }
            case DocumentAction.Open:
            {
                if (string.IsNullOrWhiteSpace(path)) return "Error: 'open' needs a path.";
                if (!SandboxPath.TryResolve(path, out var full)) return $"Error: path '{path}' escapes the workspace.";
                var r = Run($"import os\nif not os.path.exists({Py(full)}): raise FileNotFoundError('missing')\ndoc = FreeCAD.openDocument({Py(full)})\n_result_ = {{'name': doc.Name, 'objects': len(doc.Objects)}}");
                if (!r.Success) return Err(r, "open document")!;
                return $"Opened '{SandboxPath.ToAgent(path)}' as document '{Field(r, "name")}' ({Field(r, "objects")} objects).";
            }
            case DocumentAction.Save:
            {
                var target = string.IsNullOrWhiteSpace(path) ? null : SandboxPath.Resolve(path);
                var code = target == null
                    ? $"doc = FreeCAD.getDocument({Py(name)}) if {Py(name)} else FreeCAD.ActiveDocument\nif doc is None: raise ValueError('no document')\nif not doc.FileName: raise ValueError('document has no path; pass a path to save-as')\ndoc.save()\n_result_ = doc.FileName"
                    : $"doc = FreeCAD.getDocument({Py(name)}) if {Py(name)} else FreeCAD.ActiveDocument\nif doc is None: raise ValueError('no document')\ndoc.saveAs({Py(target)})\n_result_ = doc.FileName";
                var r = Run(code);
                if (!r.Success) return Err(r, "save document")!;
                var savedHost = Field(r, "path") ?? target ?? "";
                var vid = !string.IsNullOrEmpty(savedHost) ? GitSupport.Snapshot(savedHost, "FreeCADTool save") : null;
                return vid != null
                    ? $"Saved '{SandboxPath.ToAgent(savedHost)}'. New version: {vid}."
                    : $"Saved '{SandboxPath.ToAgent(savedHost)}'.";
            }
            case DocumentAction.Close:
            {
                var code = $"nm = {Py(name)} or FreeCAD.ActiveDocument.Name\nFreeCAD.closeDocument(nm)\n_result_ = {{'name': nm}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "close document")!;
                return $"Closed document '{Field(r, "name")}'.";
            }
            case DocumentAction.Recompute:
            {
                var code = $"doc = FreeCAD.getDocument({Py(name)}) if {Py(name)} else FreeCAD.ActiveDocument\nif doc is None: raise ValueError('no document')\ndoc.recompute()\n_result_ = {{'count': len(doc.Objects)}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "recompute")!;
                return $"Recomputed {Field(r, "count")} objects.";
            }
            default:
                return "Error: unsupported action.";
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Objects
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>List the objects in the active (or named) document with their names, labels, types and visibility. Use the returned names as inputs to inspect_object, edit_object, transform, boolean, etc.</summary>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Object list as JSON, or "Error:".</returns>
    public string ListObjects(string? docName = null)
    {
        Log.LogStep("FreeCADTool.ListObjects");
        var code = $"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument\nif doc is None: raise ValueError('no document')\n_result_ = [{{'name': o.Name, 'label': o.Label, 'type': o.TypeId}} for o in doc.Objects]";
        var r = Run(code);
        if (!r.Success) return Err(r, "list objects")!;
        return JsonSerializer.Serialize(r.Result.Value);
    }

    /// <summary>Inspect one object: its type, properties, and shape metrics (volume, area, validity, vertex/edge/face counts). Use to check a feature built correctly.</summary>
    /// <param name="name">Object name from list_objects.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Detailed object info as JSON, or "Error:".</returns>
    public string InspectObject(string name, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.InspectObject: {name}");
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
o = doc.getObject({Py(name)})
if o is None: raise ValueError('object not found')
props = {{}}
for p in o.PropertiesList:
    try:
        v = getattr(o, p)
        try:
            json.dumps(v)
            props[p] = v
        except Exception:
            props[p] = str(v)
    except Exception:
        props[p] = '<unreadable>'
shape = None
if hasattr(o, 'Shape') and not o.Shape.isNull():
    sh = o.Shape
    shape = {{'type': sh.ShapeType, 'volume': sh.Volume, 'area': sh.Area, 'valid': sh.isValid(), 'closed': sh.isClosed(), 'faces': len(sh.Faces), 'edges': len(sh.Edges), 'vertexes': len(sh.Vertexes)}}
_result_ = {{'name': o.Name, 'label': o.Label, 'type': o.TypeId, 'properties': props, 'shape': shape}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "inspect object")!;
        return JsonSerializer.Serialize(r.Result.Value);
    }

    /// <summary>Delete an object from the document (undoable).</summary>
    /// <param name="name">Object name from list_objects.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Confirmation or "Error:".</returns>
    public string DeleteObject(string name, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.DeleteObject: {name}");
        var code = $"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument\nif doc is None: raise ValueError('no document')\nif doc.getObject({Py(name)}) is None: raise ValueError('object not found')\ndoc.openTransaction('delete')\ntry:\n    doc.removeObject({Py(name)})\n    doc.commitTransaction()\nexcept Exception:\n    doc.abortTransaction()\n    raise\n_result_ = True";
        var r = Run(code);
        if (!r.Success) return Err(r, "delete object")!;
        return $"Deleted '{name}'.";
    }

    /// <summary>Create a primitive solid. properties is a JSON object of the FreeCAD primitive properties (see per-kind keys in the parameter doc). The primitive is added to the active document and recomputed.</summary>
    /// <param name="kind">Box | Cylinder | Sphere | Cone | Torus | Wedge | Helix.</param>
    /// <param name="properties">JSON of dimensions. Box: {"Length","Width","Height"}. Cylinder: {"Radius","Height","Angle"}. Sphere: {"Radius"}. Cone: {"Radius1","Radius2","Height","Angle"}. Torus: {"Radius1","Radius2","Angle1","Angle2","Angle"}. Wedge: {"X2","Y2","Z2","X3","Y3","Z3"}. Helix: {"Pitch","Height","Radius","Radius2","Angle","LocalCoord"}. All lengths in mm; angles in degrees. Omit keys to keep defaults.</param>
    /// <param name="name">Object name; auto-generated when omitted.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The created object name, or "Error:".</returns>
    public string CreatePrimitive(PrimitiveKind kind, string? properties = null, string? name = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.CreatePrimitive: {kind}");
        var k = kind.ToString().ToLowerInvariant();
        var typeId = kind switch
        {
            PrimitiveKind.Box => "Part::Box",
            PrimitiveKind.Cylinder => "Part::Cylinder",
            PrimitiveKind.Sphere => "Part::Sphere",
            PrimitiveKind.Cone => "Part::Cone",
            PrimitiveKind.Torus => "Part::Torus",
            PrimitiveKind.Wedge => "Part::Wedge",
            PrimitiveKind.Helix => "Part::Helix",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
doc.openTransaction('create primitive')
try:
    o = doc.addObject({Py(typeId)}, {Py(name)} or {Py(k)})
    for k, v in {PyJson(properties)}.items():
        if hasattr(o, k): setattr(o, k, v)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': o.Name, 'type': o.TypeId}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "create primitive")!;
        return $"Created {k} '{Field(r, "name")}'.";
    }

    /// <summary>Create any FreeCAD object by its type id (e.g. "Part::Box", "App::Part", "Spreadsheet::Sheet"). For primitives prefer create_primitive. properties is a JSON object of initial property values.</summary>
    /// <param name="typeId">FreeCAD type id, e.g. "Part::Box".</param>
    /// <param name="name">Object name; auto-generated when omitted.</param>
    /// <param name="properties">JSON object of initial property values.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The created object name, or "Error:".</returns>
    public string CreateObject(string typeId, string? name = null, string? properties = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.CreateObject: {typeId}");
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
doc.openTransaction('create object')
try:
    o = doc.addObject({Py(typeId)}, {Py(name)} or '')
    for k, v in {PyJson(properties)}.items():
        if hasattr(o, k): setattr(o, k, v)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': o.Name, 'type': o.TypeId}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "create object")!;
        return $"Created {typeId} '{Field(r, "name")}'.";
    }

    /// <summary>Set properties on an existing object. properties is a JSON object of property name → value (only properties that exist on the object are applied). Undoable.</summary>
    /// <param name="name">Object name from list_objects.</param>
    /// <param name="properties">JSON object of property values, e.g. {"Length": 20, "Label2": "bracket"}.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Confirmation or "Error:".</returns>
    public string EditObject(string name, string properties, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.EditObject: {name}");
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
o = doc.getObject({Py(name)})
if o is None: raise ValueError('object not found')
doc.openTransaction('edit')
try:
    for k, v in {PyJson(properties)}.items():
        if hasattr(o, k): setattr(o, k, v)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = True";
        var r = Run(code);
        if (!r.Success) return Err(r, "edit object")!;
        return $"Updated '{name}'.";
    }

    /// <summary>Move, rotate and/or scale an object via its placement. position=[x,y,z] mm; rotation=[axisX,axisY,axisZ,angleDeg]; scale=[sx,sy,sz] (uniform if one value). Any omitted component is left unchanged.</summary>
    /// <param name="name">Object name from list_objects.</param>
    /// <param name="position">JSON array [x,y,z] in mm.</param>
    /// <param name="rotation">JSON array [axisX,axisY,axisZ,angleDegrees].</param>
    /// <param name="scale">JSON array [sx,sy,sz] or single [s] for uniform scale.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Confirmation or "Error:".</returns>
    public string Transform(string name, string? position = null, string? rotation = null, string? scale = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Transform: {name}");
        var code = $@"import json, FreeCAD
from FreeCAD import Vector, Rotation, Placement
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
o = doc.getObject({Py(name)})
if o is None: raise ValueError('object not found')
pl = Placement(o.Placement)
pos = {PyJson(position)}
rot = {PyJson(rotation)}
scl = {PyJson(scale)}
if pos: pl.Base = Vector(float(pos[0]), float(pos[1]), float(pos[2]))
if rot: pl.Rotation = Rotation(Vector(float(rot[0]), float(rot[1]), float(rot[2])), float(rot[3]))
doc.openTransaction('transform')
try:
    o.Placement = pl
    if scl:
        m = FreeCAD.Matrix()
        s = scl if len(scl) == 3 else [float(scl[0]), float(scl[0]), float(scl[0])]
        m.scale(FreeCAD.Vector(s[0], s[1], s[2]))
        o.Shape = o.Shape.transformGeometry(m)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = True";
        var r = Run(code);
        if (!r.Success) return Err(r, "transform")!;
        return $"Transformed '{name}'.";
    }

    /// <summary>Boolean-combine solids. Fuse (union), Cut (first minus the rest), or Common (intersection). objectNames is a JSON array of solid object names (from list_objects). Creates a new result object.</summary>
    /// <param name="operation">Fuse | Cut | Common.</param>
    /// <param name="objectNames">JSON array of object names, e.g. ["Box","Cylinder"]. For Cut, the first is the base.</param>
    /// <param name="name">Name for the result object; auto-generated when omitted.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The result object name, or "Error:".</returns>
    public string Boolean(BooleanOperation operation, string objectNames, string? name = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Boolean: {operation}");
        var op = operation.ToString().ToLowerInvariant();
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
names = {PyJson(objectNames)}
objs = [doc.getObject(n) for n in names]
if any(o is None for o in objs): raise ValueError('one or more objects not found')
if len(objs) < 2: raise ValueError('need at least two objects')
doc.openTransaction('boolean')
try:
    base = objs[0].Shape
    others = [o.Shape for o in objs[1:]]
    if {Py(op)} == 'fuse':
        res = base.fuse(others)
    elif {Py(op)} == 'cut':
        res = base.cut(others[0]) if len(others) == 1 else base.cut(others)
    else:
        res = base.common(others[0]) if len(others) == 1 else base.common(others)
    o = doc.addObject('Part::Feature', {Py(name)} or {Py(op)})
    o.Shape = res
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': o.Name, 'valid': res.isValid(), 'volume': res.Volume}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "boolean")!;
        return $"Boolean {operation} → '{Field(r, "name")}' (valid={Field(r, "valid")}, volume={Field(r, "volume")}).";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Standard parts (workbench call-through)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Create a parametric involute gear (spur or helical) through the FCGear workbench. If FCGear is not installed it is installed automatically in the background (no user action) and the method reports that it is installing — retry in about a minute. properties: {"teeth"(int,default 15),"module"(mm,default 1),"height"(mm,default 5),"pressure_angle"(deg,default 20),"helix_angle"(deg,default 0),"shift"(float,default 0),"axle_hole"(bool),"axle_holesize"(mm,default 10)}.</summary>
    /// <param name="properties">Gear parameters JSON; all optional, FCGear defaults apply when omitted.</param>
    /// <param name="name">Label for the gear object; the created object Name is returned regardless.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The gear object name with validity and volume, an "installing" notice, or "Error:".</returns>
    public string CreateGear(string? properties = null, string? name = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.CreateGear: {properties}");
        var code = $@"import json, sys, os
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')

def _load_gears():
    try:
        import freecad.gears.commands as g
        return g
    except Exception:
        pass
    # FCGear may be installed in the user Mod dir but not on this session's startup
    # path (e.g. just auto-installed) — load it from there without restarting FreeCAD.
    try:
        import freecad, importlib
        mod = os.path.join(FreeCAD.getUserAppDataDir(), 'Mod')
        if os.path.isdir(mod):
            for cand in os.listdir(mod):
                p = os.path.join(mod, cand)
                if os.path.isdir(os.path.join(p, 'freecad', 'gears')):
                    if p not in sys.path: sys.path.insert(0, p)
                    fp = os.path.join(p, 'freecad')
                    if fp not in freecad.__path__: freecad.__path__.append(fp)
                    importlib.invalidate_caches()
                    import freecad.gears.commands as g
                    return g
    except Exception:
        pass
    return None

_gcmd = _load_gears()
if _gcmd is None:
    raise RuntimeError('FCGEAR_ABSENT')
FreeCAD.setActiveDocument(doc.Name)
p = {PyJson(properties)}
obj = _gcmd.CreateInvoluteGear.create()
if {Py(name)}: obj.Label = {Py(name)}
if 'teeth' in p: obj.num_teeth = int(p['teeth'])
if 'module' in p: obj.module = float(p['module'])
if 'height' in p: obj.height = float(p['height'])
if 'pressure_angle' in p: obj.pressure_angle = float(p['pressure_angle'])
if 'helix_angle' in p: obj.helix_angle = float(p['helix_angle'])
if 'shift' in p: obj.shift = float(p['shift'])
if 'axle_hole' in p: obj.axle_hole = bool(p['axle_hole'])
if 'axle_holesize' in p: obj.axle_holesize = float(p['axle_holesize'])
doc.recompute()
sh = getattr(obj, 'Shape', None)
_ok = sh is not None and not sh.isNull()
_result_ = {{'name': obj.Name, 'label': obj.Label, 'valid': bool(sh.isValid()) if _ok else False, 'volume': float(sh.Volume) if _ok else 0.0}}";
        var r = Run(code);
        if (!r.Success)
        {
            if ((r.ErrorTraceback ?? r.Stderr ?? "").Contains("FCGEAR_ABSENT"))
            {
                EnsureFCGearBackground();
                return FreeCADStrings.Body("GearInstalling");
            }
            return Err(r, "create_gear")!;
        }
        return $"Created gear '{Field(r, "name")}' (valid={Field(r, "valid")}, volume={Field(r, "volume")}).";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Export / import
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Export the whole document or selected objects to a CAD file. format: step, stl, 3mf, obj, or iges. path is the workspace target (e.g. /out/model.step). The file is versioned after writing.</summary>
    /// <param name="format">step | stl | 3mf | obj | iges.</param>
    /// <param name="path">Workspace path to write (extension should match format).</param>
    /// <param name="objectNames">JSON array of object names to export; omit to export all visible objects.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The exported workspace path, or "Error:".</returns>
    public string Export(string format, string path, string? objectNames = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Export: {format} → {path}");
        var extMap = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["step"] = "step", ["stp"] = "step", ["stl"] = "stl", ["3mf"] = "3mf", ["obj"] = "obj", ["iges"] = "iges", ["igs"] = "iges" };
        if (!extMap.TryGetValue(format ?? "", out var ext))
            return "Error: unknown format. Use step, stl, 3mf, obj, or iges.";
        if (!SandboxPath.TryResolve(path, out var full)) return $"Error: path '{path}' escapes the workspace.";
        var sel = string.IsNullOrWhiteSpace(objectNames)
            ? "[o for o in doc.Objects if getattr(getattr(o,'ViewObject',None),'Visibility',True)]"
            : $"[doc.getObject(n) for n in {PyJson(objectNames)}]";
        var code = $@"import json, os
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
objs = {sel}
if not objs: raise ValueError('nothing to export')
os.makedirs(os.path.dirname({Py(full)}), exist_ok=True)
import Part
Part.export(objs, {Py(full)})
_result_ = {Py(full)}";
        var r = Run(code);
        if (!r.Success) return Err(r, "export")!;
        var vid = GitSupport.Snapshot(full, "FreeCADTool export " + ext);
        return vid != null
            ? $"Exported {ext} to '{SandboxPath.ToAgent(full)}'. New version: {vid}."
            : $"Exported {ext} to '{SandboxPath.ToAgent(full)}'.";
    }

    /// <summary>Import a CAD file into the active document. Supports STEP, IGES, and mesh formats (STL/OBJ) via the Part workbench. Returns the imported object name.</summary>
    /// <param name="path">Workspace path to the file (e.g. /in/part.step).</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The imported object name, or "Error:".</returns>
    public string ImportFile(string path, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.ImportFile: {path}");
        if (!SandboxPath.TryResolve(path, out var full)) return $"Error: path '{path}' escapes the workspace.";
        var code = $@"import os
if not os.path.exists({Py(full)}): raise FileNotFoundError('missing file')
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
import Part
o = Part.Shape()
o.read({Py(full)})
feat = doc.addObject('Part::Feature', 'Imported')
feat.Shape = o
doc.recompute()
_result_ = {{'name': feat.Name, 'type': o.ShapeType}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "import")!;
        return $"Imported '{SandboxPath.ToAgent(path)}' as '{Field(r, "name")}' ({Field(r, "type")}).";
    }

    // ─────────────────────────────────────────────────────────────────────
    // Undo / redo
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Undo or redo document transactions, or report how many steps are available. Undo/redo need the FreeCAD GUI — in headless mode they return an error rather than silently doing nothing.</summary>
    /// <param name="action">Undo | Redo | Status.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Result text or "Error:".</returns>
    public string UndoRedo(UndoRedoAction action, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.UndoRedo: {action}");
        var a = action.ToString().ToLowerInvariant();
        var gui = "if not FreeCAD.GuiUp: raise RuntimeError('undo/redo requires the FreeCAD GUI (no-op in headless)')\n";
        var code = action switch
        {
            UndoRedoAction.Undo => gui + $"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument\ndoc.undo()\n_result_ = 'undone'",
            UndoRedoAction.Redo => gui + $"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument\ndoc.redo()\n_result_ = 'redone'",
            _ => $"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument\n_result_ = {{'gui': bool(FreeCAD.GuiUp), 'undo': bool(FreeCAD.GuiUp), 'redo': bool(FreeCAD.GuiUp)}}"
        };
        var r = Run(code);
        if (!r.Success) return Err(r, a)!;
        return action == UndoRedoAction.Status ? $"Undo/redo status: {JsonSerializer.Serialize(r.Result.Value)}" : $"Action '{a}' applied.";
    }
}
