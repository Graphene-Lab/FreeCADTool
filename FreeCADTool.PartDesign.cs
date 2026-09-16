using System;
using System.Text.Json;
using AIOrchestrator;

namespace AIOrchestrator.API;

/// <summary>PartDesign parametric modeling: bodies, sketches, additive/subtractive features, patterns, and edge operations.</summary>
public partial class FreeCADTool
{
    /// <summary>Create a PartDesign Body — the container for parametric feature-based modeling (sketches + pad/pocket/etc. that keep one solid). Add sketches and features to it.</summary>
    /// <param name="name">Body name; auto-generated when omitted.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The body name, or "Error:".</returns>
    public string CreateBody(string? name = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.CreateBody: {name}");
        var code = $@"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
doc.openTransaction('body')
try:
    b = doc.addObject('PartDesign::Body', {Py(name)} or 'Body')
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': b.Name}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "create body")!;
        return $"Created Body '{Field(r, "name")}'.";
    }

    /// <summary>Create a sketch and add 2D geometry to it. Create (new sketch on a plane), Rectangle, Circle, Line, Arc, Point. The sketch must exist before adding geometry (create it first). Geometry uses the sketch's local 2D coordinates (mm).</summary>
    /// <param name="action">Create | Rectangle | Circle | Line | Arc | Point.</param>
    /// <param name="sketchName">Target sketch name. For Create, the new sketch's name (auto if omitted); otherwise the existing sketch to add to.</param>
    /// <param name="body">Body to attach the sketch to (for Create). Omit for a standalone sketch.</param>
    /// <param name="plane">Attachment plane for Create: XY_Plane, XZ_Plane, YZ_Plane, or a face like "Face1". Default XY_Plane.</param>
    /// <param name="properties">Geometry JSON. Rectangle: {"x","y","width","height"}. Circle: {"cx","cy","radius"}. Line: {"x1","y1","x2","y2","construction"(bool)}. Arc: {"cx","cy","radius","startAngle","endAngle"} (deg). Point: {"x","y"}.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The sketch name, or "Error:".</returns>
    public string Sketch(SketchAction action, string? sketchName = null, string? body = null, string? plane = null, string? properties = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Sketch: {action} on {sketchName}");
        var a = action.ToString().ToLowerInvariant();
        string code = action switch
        {
            SketchAction.Create => $@"doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
doc.openTransaction('sketch create')
try:
    nm = {Py(sketchName)} or 'Sketch'
    if {Py(body)}:
        b = doc.getObject({Py(body)})
        if b is None: raise ValueError('body not found')
        sk = b.newObject('Sketcher::SketchObject', nm)
        pl = {Py(plane)} or 'XY_Plane'
        po = None
        try:
            po = b.Origin.getObject(pl)
        except Exception:
            po = None
        if po is None:
            for of in b.Origin.OriginFeatures:
                if getattr(of, 'Role', '') == pl or of.Name == pl:
                    po = of
                    break
        if po is not None:
            try:
                if hasattr(sk, 'AttachmentSupport'): sk.AttachmentSupport = [(po, [''])]
                else: sk.Support = [(po, [''])]
                sk.MapMode = 'FlatFace'
            except Exception:
                pass
    else:
        sk = doc.addObject('Sketcher::SketchObject', nm)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            SketchAction.Rectangle => $@"import Part, json
from FreeCAD import Vector
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
sk = doc.getObject({Py(sketchName)})
if sk is None: raise ValueError('sketch not found; create it first')
p = {PyJson(properties)}
x, y, w, h = float(p.get('x',0)), float(p.get('y',0)), float(p.get('width',10)), float(p.get('height',10))
doc.openTransaction('rect')
try:
    pts = [Vector(x,y,0), Vector(x+w,y,0), Vector(x+w,y+h,0), Vector(x,y+h,0)]
    for i in range(4):
        sk.addGeometry(Part.LineSegment(pts[i], pts[(i+1)%4]), False)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            SketchAction.Circle => $@"import Part, json
from FreeCAD import Vector
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
sk = doc.getObject({Py(sketchName)})
if sk is None: raise ValueError('sketch not found; create it first')
p = {PyJson(properties)}
c = Vector(float(p.get('cx',0)), float(p.get('cy',0)), 0)
rad = float(p.get('radius',5))
doc.openTransaction('circle')
try:
    sk.addGeometry(Part.Circle(c, Vector(0,0,1), rad), False)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            SketchAction.Line => $@"import Part, json
from FreeCAD import Vector
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
sk = doc.getObject({Py(sketchName)})
if sk is None: raise ValueError('sketch not found; create it first')
p = {PyJson(properties)}
constr = bool(p.get('construction', False))
doc.openTransaction('line')
try:
    sk.addGeometry(Part.LineSegment(Vector(float(p.get('x1',0)),float(p.get('y1',0)),0), Vector(float(p.get('x2',10)),float(p.get('y2',0)),0)), constr)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            SketchAction.Arc => $@"import Part, math, json
from FreeCAD import Vector
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
sk = doc.getObject({Py(sketchName)})
if sk is None: raise ValueError('sketch not found; create it first')
p = {PyJson(properties)}
c = Vector(float(p.get('cx',0)), float(p.get('cy',0)), 0)
rad = float(p.get('radius',5))
a1 = math.radians(float(p.get('startAngle',0)))
a2 = math.radians(float(p.get('endAngle',90)))
doc.openTransaction('arc')
try:
    sk.addGeometry(Part.ArcOfCircle(Part.Circle(c, Vector(0,0,1), rad), a1, a2), False)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            SketchAction.Point => $@"import Part, json
from FreeCAD import Vector
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
sk = doc.getObject({Py(sketchName)})
if sk is None: raise ValueError('sketch not found; create it first')
p = {PyJson(properties)}
doc.openTransaction('point')
try:
    sk.addGeometry(Part.Vertex(Vector(float(p.get('x',0)), float(p.get('y',0)), 0)), False)
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': sk.Name}}",

            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var res = Run(code);
        if (!res.Success) return Err(res, "sketch " + a)!;
        return action == SketchAction.Create ? $"Created sketch '{Field(res, "name")}'." : $"Added {a} to '{Field(res, "name")}'.";
    }

    /// <summary>Create a PartDesign feature from a sketch. Pad (extrude, additive), Pocket (cut, subtractive), Revolve (additive around axis), Groove (subtractive revolve), Hole (threaded/plain hole), Loft (between sketches), Sweep (profile along a spine). The sketch must be inside a Body.</summary>
    /// <param name="action">Pad | Pocket | Revolve | Groove | Hole | Loft | Sweep.</param>
    /// <param name="sketch">Profile sketch name (Pad/Pocket/Revolve/Groove/Hole) or first section (Loft).</param>
    /// <param name="body">Target Body name; omit to use the sketch's parent body.</param>
    /// <param name="properties">Feature JSON. Pad/Pocket: {"length"(mm),"reversed"(bool)}. Revolve/Groove: {"angle"(deg,default 360),"axis":"V_Axis|H_Axis|N_Axis" of the profile sketch, default V_Axis}. Hole: {"depth","diameter","threaded"(bool)}. Loft: {"sections":[sketch names],"solid"(bool)}. Sweep: {"spine"(sketch/edge name)}.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The feature name, or "Error:".</returns>
    public string Feature(FeatureAction action, string? sketch = null, string? body = null, string? properties = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Feature: {action} from {sketch}");
        var a = action.ToString().ToLowerInvariant();
        var ftype = action switch
        {
            FeatureAction.Pad => "PartDesign::Pad",
            FeatureAction.Pocket => "PartDesign::Pocket",
            FeatureAction.Revolve => "PartDesign::Revolution",
            FeatureAction.Groove => "PartDesign::Groove",
            FeatureAction.Hole => "PartDesign::Hole",
            FeatureAction.Loft => "PartDesign::AdditiveLoft",
            FeatureAction.Sweep => "PartDesign::AdditivePipe",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
sk = doc.getObject({Py(sketch)})
if sk is None: raise ValueError('sketch not found')
b = doc.getObject({Py(body)}) if {Py(body)} else sk.getParentGeoFeatureGroup()
if b is None: raise ValueError('no body; put the sketch in a Body first')
doc.openTransaction({Py(a)})
try:
    f = b.newObject({Py(ftype)}, {Py(a)})
    if f.TypeId == 'PartDesign::AdditiveLoft':
        f.Profile = sk
        secs = {PyJson(properties)}.get('sections')
        if secs: f.Sections = [doc.getObject(s) for s in secs]
    elif f.TypeId == 'PartDesign::AdditivePipe':
        f.Profile = sk
        sp = {PyJson(properties)}.get('spine')
        if sp: f.Spine = (doc.getObject(sp), [])
    else:
        f.Profile = sk
    p = {PyJson(properties)}
    if 'length' in p and hasattr(f, 'Length'): f.Length = float(p['length'])
    if 'depth' in p and hasattr(f, 'Depth'): f.Depth = float(p['depth'])
    if 'diameter' in p and hasattr(f, 'Diameter'): f.Diameter = float(p['diameter'])
    if 'threaded' in p and hasattr(f, 'Threaded'): f.Threaded = bool(p['threaded'])
    if 'angle' in p and hasattr(f, 'Angle'): f.Angle = float(p['angle'])
    if hasattr(f, 'ReferenceAxis') and f.TypeId in ('PartDesign::Revolution', 'PartDesign::Groove'):
        f.ReferenceAxis = (sk, [p.get('axis', 'V_Axis')])
    if 'reversed' in p and hasattr(f, 'Reversed'): f.Reversed = bool(p['reversed'])
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': f.Name, 'valid': f.isValid() if hasattr(f,'isValid') else True}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "feature " + a)!;
        return $"Created {a} '{Field(r, "name")}'.";
    }

    /// <summary>Pattern a feature. Linear (repeat along a direction), Polar (circular repeat), Mirror (reflect across a plane). The source feature must be in a Body.</summary>
    /// <param name="action">Linear | Polar | Mirror.</param>
    /// <param name="feature">Source feature name to pattern.</param>
    /// <param name="body">Target Body name; omit to use the feature's parent body.</param>
    /// <param name="properties">Pattern JSON. Linear: {"length"(mm),"occurrences"(int),"direction":"BaseX_Direction|BaseY_Direction|BaseZ_Direction"}. Polar: {"angle"(deg),"occurrences"(int),"axis":"V_Axis|H_Axis|N_Axis" of the source profile sketch, default N_Axis}. Mirror: {"plane":"XY_Plane|XZ_Plane|YZ_Plane"}.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The pattern feature name, or "Error:".</returns>
    public string Pattern(PatternAction action, string? feature = null, string? body = null, string? properties = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Pattern: {action} of {feature}");
        var a = action.ToString().ToLowerInvariant();
        var ptype = action switch
        {
            PatternAction.Linear => "PartDesign::LinearPattern",
            PatternAction.Polar => "PartDesign::PolarPattern",
            PatternAction.Mirror => "PartDesign::Mirrored",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
src = doc.getObject({Py(feature)})
if src is None: raise ValueError('source feature not found')
b = doc.getObject({Py(body)}) if {Py(body)} else src.getParentGeoFeatureGroup()
if b is None: raise ValueError('no body')
doc.openTransaction({Py(a + " pattern")})
try:
    f = b.newObject({Py(ptype)}, {Py(a)})
    f.Originals = [src]
    p = {PyJson(properties)}
    if 'occurrences' in p and hasattr(f, 'Occurrences'): f.Occurrences = int(p['occurrences'])
    if 'length' in p and hasattr(f, 'Length'): f.Length = float(p['length'])
    if 'angle' in p and hasattr(f, 'Angle'): f.Angle = float(p['angle'])
    if 'direction' in p and hasattr(f, 'Direction'): f.Direction = (b, [p['direction']])
    if hasattr(f, 'Axis'):
        prof = getattr(src, 'Profile', None)
        skel = prof[0] if isinstance(prof, (tuple, list)) and len(prof) > 0 else prof
        if skel is not None:
            f.Axis = (skel, [p.get('axis', 'N_Axis')])
    if 'plane' in p and hasattr(f, 'MirrorPlane'): f.MirrorPlane = (b, [p['plane']])
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': f.Name}}";
        var r = Run(code);
        if (!r.Success) return Err(r, "pattern " + a)!;
        return $"Created {a} pattern '{Field(r, "name")}'.";
    }

    /// <summary>Round (Fillet) or bevel (Chamfer) edges of a solid feature. baseObject is the solid feature; edges is a JSON array of edge references like ["Edge1","Edge3"]; size is the radius/leg in mm.</summary>
    /// <param name="operation">Fillet | Chamfer.</param>
    /// <param name="baseObject">Solid feature/object name whose edges to round/bevel.</param>
    /// <param name="edges">JSON array of edge references, e.g. ["Edge1","Edge2"]. Omit to apply to all edges.</param>
    /// <param name="size">Fillet radius / chamfer size in mm.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The resulting feature name, or "Error:".</returns>
    public string EdgeOp(EdgeOperation operation, string baseObject, string? edges = null, double size = 1.0, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.EdgeOp: {operation} on {baseObject}");
        var op = operation.ToString().ToLowerInvariant();
        var ftype = operation switch
        {
            EdgeOperation.Fillet => "Part::Fillet",
            EdgeOperation.Chamfer => "Part::Chamfer",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var code = $@"import json
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
base = doc.getObject({Py(baseObject)})
if base is None: raise ValueError('base object not found')
names = {PyJson(edges)}
if not names:
    names = ['Edge%d' % (i+1) for i in range(len(base.Shape.Edges))]
idx = [int(n.replace('Edge', '')) for n in names]
doc.openTransaction({Py(op)})
try:
    f = doc.addObject({Py(ftype)}, {Py(op)})
    f.Base = base
    f.Edges = [(i, {N(size)}, {N(size)}) for i in idx]
    doc.recompute()
    doc.commitTransaction()
except Exception:
    doc.abortTransaction()
    raise
_result_ = {{'name': f.Name, 'valid': f.Shape.isValid() if hasattr(f,'Shape') else True}}";
        var r = Run(code);
        if (!r.Success) return Err(r, op)!;
        return $"Created {op} '{Field(r, "name")}' (valid={Field(r, "valid")}).";
    }
}
