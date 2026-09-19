using System;
using System.Text.Json;
using AIOrchestrator;

namespace AIOrchestrator.API;

/// <summary>3D view control: screenshots, camera angles, zoom, object visibility, display mode and color. Screenshot and the visual properties need the FreeCAD GUI; in headless mode they return an error.</summary>
public partial class FreeCADTool
{
    /// <summary>Control the 3D view. Screenshot (save a PNG of the view to a workspace path), Angle (set camera: Isometric/Front/Back/Top/Bottom/Left/Right), Fit (zoom to fit all), Zoom (in/out by factor), Visibility (show/hide an object), DisplayMode (Shaded/Wireframe/Points), Color (set object RGB). Screenshot, Visibility, DisplayMode and Color need the GUI.</summary>
    /// <param name="action">Screenshot | Angle | Fit | Zoom | Visibility | DisplayMode | Color.</param>
    /// <param name="path">Workspace target for Screenshot (e.g. /view.png). Default /freecad_view.png.</param>
    /// <param name="properties">JSON. Angle: {"angle":"Isometric|Front|Back|Top|Bottom|Left|Right"}. Zoom: {"factor":1.5}. Visibility: {"object":"Box","visible":true}. DisplayMode: {"object":"Box","mode":"Shaded|Wireframe|Points"}. Color: {"object":"Box","r":0.8,"g":0.2,"b":0.2} (0..1).</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>Result text (Screenshot returns the workspace path) or "Error:".</returns>
    public string View(ViewAction action, string? path = null, string? properties = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.View: {action}");
        switch (action)
        {
            case ViewAction.Screenshot:
            {
                var target = SandboxPath.Resolve(string.IsNullOrWhiteSpace(path) ? "/freecad_view.png" : path);
                var code = $@"import os
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available — screenshot needs the FreeCAD GUI')
import FreeCADGui
view = FreeCADGui.ActiveDocument.ActiveView
if view is None: raise ValueError('no active 3D view')
os.makedirs(os.path.dirname({Py(target)}), exist_ok=True)
FreeCADGui.updateGui()
view.saveImage({Py(target)}, 1000, 800, 'White')
FreeCADGui.updateGui()
_result_ = {Py(target)}";
                var r = Run(code);
                if (!r.Success) return Err(r, "screenshot")!;
                var vid = GitSupport.Snapshot(target, "FreeCADTool screenshot");
                return vid != null
                    ? $"Saved view image to '{SandboxPath.ToAgent(target)}'. New version: {vid}."
                    : $"Saved view image to '{SandboxPath.ToAgent(target)}'.";
            }
            case ViewAction.Angle:
            {
                var code = $@"import json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')
import FreeCADGui
view = FreeCADGui.ActiveDocument.ActiveView
p = {PyJson(properties)}
ang = p.get('angle', 'Isometric')
m = {{'Isometric': view.viewIsometric, 'Front': view.viewFront, 'Back': view.viewRear, 'Top': view.viewTop, 'Bottom': view.viewBottom, 'Left': view.viewLeft, 'Right': view.viewRight}}
if ang not in m: raise ValueError('unknown angle')
m[ang]()
view.fitAll()
_result_ = {{'angle': ang}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "set angle")!;
                return $"View set to {Field(r, "angle")}.";
            }
            case ViewAction.Fit:
            {
                var code = "if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')\nimport FreeCADGui\nFreeCADGui.ActiveDocument.ActiveView.fitAll()\n_result_ = True";
                var r = Run(code);
                if (!r.Success) return Err(r, "fit all")!;
                return "Zoomed to fit all.";
            }
            case ViewAction.Zoom:
            {
                var code = $@"import json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')
import FreeCADGui
view = FreeCADGui.ActiveDocument.ActiveView
p = {PyJson(properties)}
f = float(p.get('factor', 1.5))
if f >= 1: view.zoomIn()
else: view.zoomOut()
_result_ = f";
                var r = Run(code);
                if (!r.Success) return Err(r, "zoom")!;
                return "Zoom adjusted.";
            }
            case ViewAction.Visibility:
            {
                var code = $@"import json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
p = {PyJson(properties)}
o = doc.getObject(p['object'])
if o is None: raise ValueError('object not found')
o.ViewObject.Visibility = bool(p.get('visible', True))
_result_ = {{'result': o.Name}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "set visibility")!;
                return $"Visibility set for '{Field(r, "result")}'.";
            }
            case ViewAction.DisplayMode:
            {
                var code = $@"import json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
p = {PyJson(properties)}
o = doc.getObject(p['object'])
if o is None: raise ValueError('object not found')
o.ViewObject.DisplayMode = p.get('mode', 'Shaded')
_result_ = {{'mode': o.ViewObject.DisplayMode}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "set display mode")!;
                return $"Display mode set to {Field(r, "mode")}.";
            }
            case ViewAction.Color:
            {
                var code = $@"import json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available')
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
p = {PyJson(properties)}
o = doc.getObject(p['object'])
if o is None: raise ValueError('object not found')
o.ViewObject.ShapeColor = (float(p.get('r',0.8)), float(p.get('g',0.8)), float(p.get('b',0.8)))
_result_ = {{'result': o.Name}}";
                var r = Run(code);
                if (!r.Success) return Err(r, "set color")!;
                return $"Color set for '{Field(r, "result")}'.";
            }
            case ViewAction.Render:
            {
                var target = SandboxPath.Resolve(string.IsNullOrWhiteSpace(path) ? "/freecad_render.png" : path);
                var r = RenderToPng(target, properties, docName);
                if (!r.Success) return Err(r, "render view")!;
                var vid = GitSupport.Snapshot(target, "FreeCADTool render");
                return vid != null
                    ? $"Rendered view to '{SandboxPath.ToAgent(target)}'. New version: {vid}."
                    : $"Rendered view to '{SandboxPath.ToAgent(target)}'.";
            }
            default:
                return "Error: unsupported action.";
        }
    }

    // Deterministic offscreen render of the model (or one object) to a PNG: a fixed
    // axonometric angle, fit-to-object framing, a clean background, and hide-aware — only
    // visible objects render, and when an object is named every other visible object is
    // hidden for the shot and restored afterwards. This is a controlled render of the CAD
    // geometry through the view's own saveImage, not a grab of the FreeCAD window: the
    // design choice for feeding CAD views to a vision model.
    // properties JSON: {object?, angle?, zoom?, width?, height?, background?}.
    private ExecResult RenderToPng(string target, string? properties, string? docName)
    {
        var code = $@"import FreeCAD, FreeCADGui, os, json
if not FreeCAD.GuiUp: raise RuntimeError('GUI not available — render needs the FreeCAD GUI')
doc = FreeCAD.getDocument({Py(docName)}) if {Py(docName)} else FreeCAD.ActiveDocument
if doc is None: raise ValueError('no document')
view = FreeCADGui.ActiveDocument.ActiveView
if view is None: raise ValueError('no active 3D view')
p = {PyJson(properties)}
target = p.get('object')
angle = p.get('angle', 'Isometric')
zoom = float(p.get('zoom', 1.0))
W = int(p.get('width', 1200)); H = int(p.get('height', 1200))
bg = p.get('background', 'White')
m = {{'Isometric': view.viewIsometric, 'Front': view.viewFront, 'Back': view.viewRear, 'Top': view.viewTop, 'Bottom': view.viewBottom, 'Left': view.viewLeft, 'Right': view.viewRight}}
if angle not in m: raise ValueError('unknown angle: ' + str(angle))
restored = []
if target:
    o = doc.getObject(target)
    if o is None: raise ValueError('object not found: ' + str(target))
    for x in doc.Objects:
        vo = getattr(x, 'ViewObject', None)
        if vo is not None and x.Name != o.Name and vo.Visibility:
            restored.append(x.Name); vo.Visibility = False
    nrender = 1
else:
    nrender = sum(1 for x in doc.Objects if getattr(x, 'ViewObject', None) is not None and x.ViewObject.Visibility)
try:
    m[angle]()
    view.fitAll()
    if zoom != 1.0:
        try:
            cam = view.getCameraNode(); cam.height = float(cam.height.getValue() / zoom)
        except Exception: pass
    FreeCADGui.updateGui()
    os.makedirs(os.path.dirname({Py(target)}), exist_ok=True)
    view.saveImage({Py(target)}, W, H, bg)
finally:
    for n in restored:
        try: doc.getObject(n).ViewObject.Visibility = True
        except Exception: pass
FreeCADGui.updateGui()
_result_ = {{'path': {Py(target)}, 'objects': nrender, 'angle': angle}}";
        return Run(code);
    }

    /// <summary>Render the model (or one object) and ask the vision model to describe it — use to visually verify a complex part looks correct. Needs a vision-capable provider (Vision level LanguageModel); returns a clear message when vision is unavailable.</summary>
    /// <param name="objectName">Object to isolate and describe; omit to describe all visible objects.</param>
    /// <param name="question">What to ask about the render. Default asks for shape, features, proportions and whether the view is cut off / too small / empty.</param>
    /// <param name="angle">Camera angle: Isometric (default) | Front | Back | Top | Bottom | Left | Right.</param>
    /// <param name="docName">Document name; omit for the active document.</param>
    /// <returns>The vision model's description, or a "vision not available" / error message.</returns>
    public string Describe(string? objectName = null, string? question = null, string? angle = null, string? docName = null)
    {
        Log.LogStep($"FreeCADTool.Describe: {objectName ?? "<all visible>"}");
        if (!Vision.HasLanguageVision)
            return $"Vision is not available (level: {Vision.Level}). Enable 'Supports vision' on the provider to get a visual analysis.";
        var q = string.IsNullOrWhiteSpace(question)
            ? "Describe this 3D CAD render: what object is shown, its overall shape, main features and proportions. State clearly if the object is cut off at the frame edge, too small to judge, or the view is empty."
            : question;
        var d = new Dictionary<string, object> { ["angle"] = string.IsNullOrWhiteSpace(angle) ? "Isometric" : angle };
        if (!string.IsNullOrWhiteSpace(objectName)) d["object"] = objectName;
        var target = SandboxPath.Resolve("/freecad_describe.png");
        var r = RenderToPng(target, JsonSerializer.Serialize(d), docName);
        if (!r.Success) return Err(r, "render for describe")!;
        try
        {
            return Vision.AskFileAsync(target, q).GetAwaiter().GetResult();
        }
        catch (Exception ex) { return $"Error: vision describe failed — {ex.Message}"; }
    }
}
