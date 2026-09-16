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
_result_ = o.Name";
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
_result_ = o.Name";
                var r = Run(code);
                if (!r.Success) return Err(r, "set color")!;
                return $"Color set for '{Field(r, "result")}'.";
            }
            default:
                return "Error: unsupported action.";
        }
    }
}
