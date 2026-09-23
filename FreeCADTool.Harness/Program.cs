using System.Text.Json;
using AIOrchestrator;
using AIOrchestrator.API;

// End-to-end harness for FreeCADTool against a live headless FreeCAD bridge (127.0.0.1:9876).
// Do not let create_gear trigger a real FCGear download / Mod-dir write during the test run.
Environment.SetEnvironmentVariable("FREECAD_DISABLE_AUTOINSTALL", "1");
var ws = Path.Combine(Path.GetTempPath(), "freectool_ws_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(ws);
Setup.DocumentsPath = ws;
Console.WriteLine("Workspace: " + ws);

var tool = new FreeCADTool();
int pass = 0, fail = 0;
void Check(string label, string result, Func<string, bool> assert)
{
    bool ok;
    try { ok = assert(result); } catch (Exception ex) { ok = false; result += " [assert threw: " + ex.Message + "]"; }
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}: {result}");
    if (ok) pass++; else fail++;
}
bool NoErr(string s) => !s.StartsWith("Error:");

Check("status", tool.Status(), NoErr);
Check("create doc", tool.Document(FreeCADTool.DocumentAction.Create, "test"), NoErr);
Check("create box", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":20,\"Height\":30}"), NoErr);

Check("inspect box volume=6000", tool.InspectObject("box"), r =>
{
    var d = JsonDocument.Parse(r).RootElement.GetProperty("shape").GetProperty("volume").GetDouble();
    return Math.Abs(d - 6000) < 1;
});

Check("create cylinder", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":5,\"Height\":40}"), NoErr);
Check("boolean cut", tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"box\",\"cylinder\"]"), NoErr);
Check("export step", tool.Export("step", "/out/test.step"), r =>
    NoErr(r) && File.Exists(Path.Combine(ws, "out", "test.step")));

// ── C-007: export surfaces the object count and any requested names not in the document.
//    A bad objectNames used to silently write a header-only STEP with no geometry. ──
Check("export reports the object count",
    tool.Export("step", "/out/box.step", "[\"box\"]"),
    r => NoErr(r) && r.Contains("(1 object)"));
Check("export reports a missing object name",
    tool.Export("step", "/out/mix.step", "[\"box\",\"ghost\"]"),
    r => NoErr(r) && r.Contains("Missing objects") && r.Contains("ghost"));
Check("export with all-missing names errors (nothing to export)",
    tool.Export("step", "/out/none.step", "[\"ghost1\",\"ghost2\"]"),
    r => r.StartsWith("Error:"));

// PartDesign: body + sketch + pad
Check("create body", tool.CreateBody("b"), NoErr);
Check("sketch create", tool.Sketch(FreeCADTool.SketchAction.Create, "sk", "b", "XY_Plane"), NoErr);
Check("sketch rectangle", tool.Sketch(FreeCADTool.SketchAction.Rectangle, "sk", properties: "{\"x\":0,\"y\":0,\"width\":20,\"height\":10}"), NoErr);
Check("pad", tool.Feature(FreeCADTool.FeatureAction.Pad, "sk", "b", "{\"length\":15}"), NoErr);
Check("pad volume=3000", tool.InspectObject("pad"), r =>
{
    var d = JsonDocument.Parse(r).RootElement.GetProperty("shape").GetProperty("volume").GetDouble();
    return Math.Abs(d - 3000) < 1;
});

// ── H1: create/feature surface the kernel read-back (valid + volume) so the agent
//    sees the truth without a follow-up inspect_object, and a failed feature is not
//    reported as a silent success. ──
Check("create_primitive reports valid+volume (no follow-up inspect)",
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":20,\"Height\":30}", "boxm"),
    r => NoErr(r) && r.Contains("valid=True") && r.Contains("volume=6000"));
tool.CreateBody("vb");
tool.Sketch(FreeCADTool.SketchAction.Create, "vsk", "vb", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Rectangle, "vsk", properties: "{\"x\":0,\"y\":0,\"width\":10,\"height\":10}");
Check("Feature pad reports valid+volume",
    tool.Feature(FreeCADTool.FeatureAction.Pad, "vsk", "vb", "{\"length\":10}"),
    r =>
    {
        if (!NoErr(r) || !r.Contains("valid=True")) return false;
        var i = r.IndexOf("volume="); if (i < 0) return false;
        var j = r.IndexOf(')', i);
        var v = double.Parse(r.Substring(i + 7, j - (i + 7)), System.Globalization.CultureInfo.InvariantCulture);
        return Math.Abs(v - 1000) < 1;
    });
// A pad from an OPEN (single-line) sketch must be reported INVALID, not as a silent
// "Created pad" (the old code computed f.isValid() but dropped it from the result).
tool.CreateBody("opb");
tool.Sketch(FreeCADTool.SketchAction.Create, "opensk", "opb", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Line, "opensk", properties: "{\"x1\":0,\"y1\":0,\"x2\":10,\"y2\":10}");
Check("pad from open sketch reported INVALID (not silent success)",
    tool.Feature(FreeCADTool.FeatureAction.Pad, "opensk", "opb", "{\"length\":5}"),
    r => r.Contains("INVALID") && !r.Contains("valid=True"));

// ── edit / transform ──
Check("edit_object box Length=20", tool.EditObject("box", "{\"Length\":20}"), NoErr);
Check("create box2", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "box2"), NoErr);
Check("transform box2 move+rot+scale", tool.Transform("box2", position: "[5,0,0]", rotation: "[0,0,1,45]", scale: "[2,1,1]"), NoErr);

// ── C-004: create_primitive optional position places the primitive in one call
//    (no follow-up transform). The Placement must reflect the offset and the shape
//    must still be valid. ──
Check("create_primitive with position is valid",
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "posbox", null, "[5,10,15]"),
    r => NoErr(r) && r.Contains("valid=True"));
Check("position reflected in the object Placement",
    tool.InspectObject("posbox"),
    r =>
    {
        if (r.StartsWith("Error:")) return false;
        var pl = JsonDocument.Parse(r).RootElement.GetProperty("properties").GetProperty("Placement").GetString();
        return pl != null && pl.Contains("5") && pl.Contains("10") && pl.Contains("15");
    });
Check("create_primitive without position stays at origin",
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "origbox"),
    r =>
    {
        if (!NoErr(r)) return false;
        var pl = JsonDocument.Parse(tool.InspectObject("origbox")).RootElement.GetProperty("properties").GetProperty("Placement").GetString();
        return pl != null && pl.Contains("Pos=(0,0,0)") && !pl.Contains("Pos=(5");
    });

// ── C-006: property-blob methods surface the keys the kernel ignored. A typo'd property
//    used to silently leave the object at its default (S2 wrong-dimension); now the bad key
//    is named so the agent can fix it. ──
Check("create_primitive reports a typo'd property as ignored",
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Lenght\":99,\"Width\":20}", "typobox"),
    r => NoErr(r) && r.Contains("Ignored unknown properties:") && r.Contains("Lenght"));
Check("create_primitive with all-valid properties reports no ignored",
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "okbox"),
    r => NoErr(r) && !r.Contains("Ignored"));
Check("edit_object reports a typo'd property as ignored",
    tool.EditObject("okbox", "{\"Lenght\":50,\"Height\":40}"),
    r => NoErr(r) && r.Contains("Ignored unknown properties:") && r.Contains("Lenght"));
Check("create_object reports a typo'd property as ignored",
    tool.CreateObject("Part::Box", "co1", "{\"Lenght\":7}"),
    r => NoErr(r) && r.Contains("Ignored unknown properties:") && r.Contains("Lenght"));

// ── edge ops ──
Check("create box3", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box3"), NoErr);
Check("fillet box3 all edges", tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "box3", size: 2.0), r => NoErr(r) && r.Contains("valid=True"));
Check("create box4", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box4"), NoErr);
Check("chamfer box4 two edges", tool.EdgeOp(FreeCADTool.EdgeOperation.Chamfer, "box4", edges: "[\"Edge1\",\"Edge2\"]", size: 2.0), NoErr);

// ── H2: EdgeOp surfaces the kernel read-back (volume) and flags an over-large op ──
Check("create box5", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box5"), NoErr);
Check("EdgeOp fillet reports volume", tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "box5", size: 2.0),
    r => NoErr(r) && r.Contains("valid=True") && r.Contains("volume="));
Check("create box6", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box6"), NoErr);
Check("over-large fillet reported INVALID (not silent success)", tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "box6", size: 15.0),
    r => r.Contains("INVALID") && !r.Contains("valid=True"));

// ── C-003: inspect_object optional per-edge descriptors (edge selection by geometry) ──
Check("inspect edgeDetails returns per-edge descriptors",
    tool.InspectObject("boxm", null, true),
    r =>
    {
        if (r.StartsWith("Error:")) return false;
        var ed = JsonDocument.Parse(r).RootElement.GetProperty("edges_detail");
        if (ed.GetProperty("total").GetInt32() != 12) return false;
        bool has10 = false, has20 = false, has30 = false;
        foreach (var it in ed.GetProperty("items").EnumerateArray())
        {
            var L = it.GetProperty("length").GetDouble();
            if (Math.Abs(L - 10) < 0.01) has10 = true;
            if (Math.Abs(L - 20) < 0.01) has20 = true;
            if (Math.Abs(L - 30) < 0.01) has30 = true;
        }
        return has10 && has20 && has30;
    });
Check("inspect without edgeDetails omits edges_detail",
    tool.InspectObject("boxm"),
    r => NoErr(r) && !r.Contains("edges_detail"));
// End-to-end: choose the vertical edge (length 30) by its descriptor and fillet exactly it.
string chosenEdge = null;
foreach (var it in JsonDocument.Parse(tool.InspectObject("boxm", null, true)).RootElement.GetProperty("edges_detail").GetProperty("items").EnumerateArray())
    if (Math.Abs(it.GetProperty("length").GetDouble() - 30) < 0.01) { chosenEdge = it.GetProperty("ref").GetString(); break; }
Check("fillet the edge chosen by its descriptor",
    chosenEdge != null ? tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "boxm", edges: "[\"" + chosenEdge + "\"]", size: 2.0) : "no edge chosen",
    r => NoErr(r) && r.Contains("valid=True"));

// ── revolve (default sketch vertical axis) ──
Check("create body rev", tool.CreateBody("revb"), NoErr);
Check("sketch create rev (XZ)", tool.Sketch(FreeCADTool.SketchAction.Create, "revsk", "revb", "XZ_Plane"), NoErr);
Check("sketch rect rev offset", tool.Sketch(FreeCADTool.SketchAction.Rectangle, "revsk", properties: "{\"x\":5,\"y\":0,\"width\":10,\"height\":10}"), NoErr);
Check("revolve 360", tool.Feature(FreeCADTool.FeatureAction.Revolve, "revsk", "revb", "{\"angle\":360}"), NoErr);
Check("revolve volume (tube)", tool.InspectObject("revolve"), r =>
{
    var sh = JsonDocument.Parse(r).RootElement.GetProperty("shape");
    return sh.ValueKind != JsonValueKind.Null && Math.Abs(sh.GetProperty("volume").GetDouble() - Math.PI * (15 * 15 - 5 * 5) * 10) < 5;
});

// ── pocket (dedicated body: pad then cut a hole) ──
Check("create body pk", tool.CreateBody("pkb"), NoErr);
Check("sketch create pk base", tool.Sketch(FreeCADTool.SketchAction.Create, "pksk", "pkb", "XY_Plane"), NoErr);
Check("sketch rect pk base", tool.Sketch(FreeCADTool.SketchAction.Rectangle, "pksk", properties: "{\"x\":0,\"y\":0,\"width\":20,\"height\":20}"), NoErr);
Check("pad pk base", tool.Feature(FreeCADTool.FeatureAction.Pad, "pksk", "pkb", "{\"length\":10}"), NoErr);
Check("sketch create pk hole", tool.Sketch(FreeCADTool.SketchAction.Create, "pksk2", "pkb", "XY_Plane"), NoErr);
Check("sketch circle pk hole", tool.Sketch(FreeCADTool.SketchAction.Circle, "pksk2", properties: "{\"cx\":10,\"cy\":10,\"radius\":3}"), NoErr);
Check("pocket hole", tool.Feature(FreeCADTool.FeatureAction.Pocket, "pksk2", "pkb", "{\"length\":10,\"reversed\":true}"), NoErr);

// ── patterns (default direction/axis) ──
Check("pattern linear pad", tool.Pattern(FreeCADTool.PatternAction.Linear, "pad", "b", "{\"length\":30,\"occurrences\":3}"), NoErr);
Check("pattern polar pad", tool.Pattern(FreeCADTool.PatternAction.Polar, "pad", "b", "{\"angle\":120,\"occurrences\":3}"), NoErr);
Check("polar pattern shape valid", tool.InspectObject("polar"), r =>
{
    var sh = JsonDocument.Parse(r).RootElement.GetProperty("shape");
    return sh.ValueKind != JsonValueKind.Null && sh.GetProperty("valid").GetBoolean();
});

// ── C-005: Pattern surfaces the kernel read-back (valid + volume) so the agent sees the
//    truth without a follow-up inspect_object (same round-trip removal as C-001). The shared
//    null-guard is already proven to catch an invalid shape on Feature/EdgeOp; a reliably
//    invalid PATTERN input is not controllable (negative-length invalidity is property-set-order
//    dependent in the kernel), so only the read-back surface is asserted here. ──
tool.CreateBody("patb");
tool.Sketch(FreeCADTool.SketchAction.Create, "patsk", "patb", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Rectangle, "patsk", properties: "{\"x\":0,\"y\":0,\"width\":10,\"height\":10}");
string patPad = System.Text.RegularExpressions.Regex.Match(
    tool.Feature(FreeCADTool.FeatureAction.Pad, "patsk", "patb", "{\"length\":10}"), "'([^']+)'").Groups[1].Value;
Check("Pattern linear reports valid+volume (no follow-up inspect)",
    tool.Pattern(FreeCADTool.PatternAction.Linear, patPad, "patb", "{\"length\":30,\"occurrences\":2}"),
    r => NoErr(r) && r.Contains("valid=True") && r.Contains("volume="));
Check("Pattern polar reports valid+volume (no follow-up inspect)",
    tool.Pattern(FreeCADTool.PatternAction.Polar, patPad, "patb", "{\"angle\":90,\"occurrences\":2}"),
    r => NoErr(r) && r.Contains("valid=True") && r.Contains("volume="));

// ── undo / redo (GUI-only: in headless they must report the GUI requirement, not silently no-op) ──
Check("undo status reports gui flag", tool.UndoRedo(FreeCADTool.UndoRedoAction.Status), r => NoErr(r) && r.Contains("gui"));
Check("undo requires GUI (headless errors)", tool.UndoRedo(FreeCADTool.UndoRedoAction.Undo), r => r.Contains("GUI"));
Check("redo requires GUI (headless errors)", tool.UndoRedo(FreeCADTool.UndoRedoAction.Redo), r => r.Contains("GUI"));

// ── standard parts: FCGear call-through. Passes when FCGear is installed (valid gear) or
//    absent (the "installing" notice; auto-install is disabled in the harness so no real download).
//    The gear-build path itself is verified separately with a freecadcmd + FCGear run. ──
Check("create_gear (FCGear) responds correctly", tool.CreateGear("{\"teeth\":20,\"module\":2,\"height\":8}", "gear"), r =>
    (NoErr(r) && r.Contains("valid=True")) || r.Contains("FCGear"));

// ── import roundtrip ──
Check("import step", tool.ImportFile("/out/test.step"), NoErr);

// ── save (regression for issue #15: the old code read a bare-string result with Field(),
//    which threw InvalidOperationException on a non-object; now the result is a dict and the
//    saved path must be surfaced, never an empty Saved '') ──
Check("save-as surfaces the path", tool.Document(FreeCADTool.DocumentAction.Save, null, "/test_saved.FCStd"), r =>
    NoErr(r) && r.Contains("Saved '") && !r.Contains("Saved ''"));
Check("save (no path) surfaces the path", tool.Document(FreeCADTool.DocumentAction.Save), r =>
    NoErr(r) && r.Contains("Saved '") && !r.Contains("Saved ''"));

// ── recompute / close (regression: must surface count/name, not empty) ──
Check("recompute returns count", tool.Document(FreeCADTool.DocumentAction.Recompute), r => NoErr(r) && r.Contains("objects") && !r.StartsWith("Recomputed  "));
Check("close returns name", tool.Document(FreeCADTool.DocumentAction.Close), r => NoErr(r) && r.Contains("Closed document '") && !r.Contains("Closed document ''"));

Console.WriteLine($"\n==== {pass} passed, {fail} failed ====");
Console.Out.Flush();
return fail == 0 ? 0 : 1;
