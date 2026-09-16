using System.Text.Json;
using AIOrchestrator;
using AIOrchestrator.API;

// End-to-end harness for FreeCADTool against a live headless FreeCAD bridge (127.0.0.1:9876).
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

// ── edit / transform ──
Check("edit_object box Length=20", tool.EditObject("box", "{\"Length\":20}"), NoErr);
Check("create box2", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "box2"), NoErr);
Check("transform box2 move+rot+scale", tool.Transform("box2", position: "[5,0,0]", rotation: "[0,0,1,45]", scale: "[2,1,1]"), NoErr);

// ── edge ops ──
Check("create box3", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box3"), NoErr);
Check("fillet box3 all edges", tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "box3", size: 2.0), r => NoErr(r) && r.Contains("valid=True"));
Check("create box4", tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "box4"), NoErr);
Check("chamfer box4 two edges", tool.EdgeOp(FreeCADTool.EdgeOperation.Chamfer, "box4", edges: "[\"Edge1\",\"Edge2\"]", size: 2.0), NoErr);

// ── revolve (default sketch vertical axis) ──
Check("create body rev", tool.CreateBody("revb"), NoErr);
Check("sketch create rev (XZ)", tool.Sketch(FreeCADTool.SketchAction.Create, "revsk", "revb", "XZ_Plane"), NoErr);
Check("sketch rect rev offset", tool.Sketch(FreeCADTool.SketchAction.Rectangle, "revsk", properties: "{\"x\":5,\"y\":0,\"width\":10,\"height\":10}"), NoErr);
Check("revolve 360", tool.Feature(FreeCADTool.FeatureAction.Revolve, "revsk", "revb", "{\"angle\":360}"), NoErr);

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

// ── undo / redo ──
Check("undo status", tool.UndoRedo(FreeCADTool.UndoRedoAction.Status), NoErr);
Check("undo", tool.UndoRedo(FreeCADTool.UndoRedoAction.Undo), NoErr);
Check("redo", tool.UndoRedo(FreeCADTool.UndoRedoAction.Redo), NoErr);

// ── import roundtrip ──
Check("import step", tool.ImportFile("/out/test.step"), NoErr);

// ── recompute / close (regression: must surface count/name, not empty) ──
Check("recompute returns count", tool.Document(FreeCADTool.DocumentAction.Recompute), r => NoErr(r) && r.Contains("objects") && !r.StartsWith("Recomputed  "));
Check("close returns name", tool.Document(FreeCADTool.DocumentAction.Close), r => NoErr(r) && r.Contains("Closed document '") && !r.Contains("Closed document ''"));

Console.WriteLine($"\n==== {pass} passed, {fail} failed ====");
Console.Out.Flush();
return fail == 0 ? 0 : 1;
