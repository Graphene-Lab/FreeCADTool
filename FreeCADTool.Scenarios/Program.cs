using System.Text.Json;
using AIOrchestrator;
using AIOrchestrator.API;

// Field tests: realistic agent-level CAD tasks (creation + editing) driven through the
// FreeCADTool method surface, each verified against expected geometry. This checks that the
// tool is expressive enough for an agent to accomplish real modeling goals, not just that
// each method runs.
var ws = Path.Combine(Path.GetTempPath(), "freecad_scen_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(ws);
Setup.DocumentsPath = ws;
Console.WriteLine("Workspace: " + ws);

var tool = new FreeCADTool();
int pass = 0, fail = 0;

// The bridge persists documents across runs; close every open document by name so the
// fixed doc names below start from a clean slate (Close only closes the active doc, so
// enumerate them first).
var lst = tool.Document(FreeCADTool.DocumentAction.List);
int br = lst.IndexOf('[');
if (br >= 0)
{
    using var jd = JsonDocument.Parse(lst.Substring(br));
    foreach (var e in jd.RootElement.EnumerateArray())
        if (e.TryGetProperty("name", out var nm) && nm.GetString() is { } s)
            tool.Document(FreeCADTool.DocumentAction.Close, s);
}

void Check(string label, bool ok, string detail)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}: {detail}");
    if (ok) pass++; else fail++;
}
bool NoErr(string s) => !s.StartsWith("Error:");
double Vol(string inspectJson)
{
    if (inspectJson.StartsWith("Error:")) return double.NaN;
    using var d = JsonDocument.Parse(inspectJson);
    if (!d.RootElement.TryGetProperty("shape", out var sh) || sh.ValueKind == JsonValueKind.Null) return double.NaN;
    return sh.GetProperty("volume").GetDouble();
}
bool Valid(string inspectJson)
{
    if (inspectJson.StartsWith("Error:")) return false;
    using var d = JsonDocument.Parse(inspectJson);
    return d.RootElement.TryGetProperty("shape", out var sh) && sh.ValueKind != JsonValueKind.Null
        && sh.GetProperty("valid").GetBoolean();
}
void Fresh(string name) => tool.Document(FreeCADTool.DocumentAction.Create, name);

const double PI = Math.PI;

// ── Task 1: "a flat mounting bracket 60x40x5 with a 5mm hole in each corner" (creation) ──
Fresh("t1");
tool.CreateBody("brk");
tool.Sketch(FreeCADTool.SketchAction.Create, "base", "brk", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Rectangle, "base", properties: "{\"x\":0,\"y\":0,\"width\":60,\"height\":40}");
tool.Feature(FreeCADTool.FeatureAction.Pad, "base", "brk", "{\"length\":5}");
tool.Sketch(FreeCADTool.SketchAction.Create, "holes", "brk", "XY_Plane");
foreach (var (hx, hy) in new[] { (10.0, 10.0), (50.0, 10.0), (10.0, 30.0), (50.0, 30.0) })
    tool.Sketch(FreeCADTool.SketchAction.Circle, "holes", properties: $"{{\"cx\":{hx},\"cy\":{hy},\"radius\":2.5}}");
var pk = tool.Feature(FreeCADTool.FeatureAction.Pocket, "holes", "brk", "{\"length\":5,\"reversed\":true}");
double t1v = Vol(tool.InspectObject("pocket", "t1"));
double t1exp = 60 * 40 * 5 - 4 * PI * 2.5 * 2.5 * 5; // 12000 - 392.70
Check("T1 bracket 4 corner holes", NoErr(pk) && Math.Abs(t1v - t1exp) < 2, $"volume={t1v:F1} expected≈{t1exp:F1}");

// ── Task 2: "a pipe, 30mm OD, 20mm ID, 50mm long" (creation, boolean) ──
Fresh("t2");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":15,\"Height\":50}", "outer");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":10,\"Height\":60}", "bore");
var cut = tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"outer\",\"bore\"]", "pipe", "t2");
double t2v = Vol(tool.InspectObject("pipe", "t2"));
double t2exp = PI * (15 * 15 - 10 * 10) * 50; // 19634.95
Check("T2 pipe OD30/ID20/L50", NoErr(cut) && Math.Abs(t2v - t2exp) < 2, $"volume={t2v:F1} expected≈{t2exp:F1}");

// ── Task 3: "a stepped shaft: 20mm dia x 30mm, then 12mm dia x 20mm on top" (creation) ──
Fresh("t3");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":10,\"Height\":30}", "step1");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":6,\"Height\":20}", "step2");
tool.Transform("step2", position: "[0,0,30]");
var fuse3 = tool.Boolean(FreeCADTool.BooleanOperation.Fuse, "[\"step1\",\"step2\"]", "shaft", "t3");
double t3v = Vol(tool.InspectObject("shaft", "t3"));
double t3exp = PI * 10 * 10 * 30 + PI * 6 * 6 * 20; // 9424.78 + 2261.95
Check("T3 stepped shaft", NoErr(fuse3) && Math.Abs(t3v - t3exp) < 2, $"volume={t3v:F1} expected≈{t3exp:F1}");

// ── Task 4: "make the pad twice as tall" (editing an existing feature) ──
Fresh("t4");
tool.CreateBody("b4");
tool.Sketch(FreeCADTool.SketchAction.Create, "s4", "b4", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Rectangle, "s4", properties: "{\"x\":0,\"y\":0,\"width\":20,\"height\":10}");
tool.Feature(FreeCADTool.FeatureAction.Pad, "s4", "b4", "{\"length\":10}");
double before = Vol(tool.InspectObject("pad", "t4"));
tool.EditObject("pad", "{\"Length\":20}");
double after = Vol(tool.InspectObject("pad", "t4"));
Check("T4 edit pad length 10->20", Math.Abs(before - 2000) < 1 && Math.Abs(after - 4000) < 1, $"before={before:F1} after={after:F1}");

// ── Task 5: "round all the edges of a 20mm cube with a 2mm fillet" (creation + edge op) ──
Fresh("t5");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":20,\"Width\":20,\"Height\":20}", "cube");
var fil = tool.EdgeOp(FreeCADTool.EdgeOperation.Fillet, "cube", size: 2.0, docName: "t5");
double t5v = Vol(tool.InspectObject("fillet", "t5"));
Check("T5 filleted cube", NoErr(fil) && Valid(tool.InspectObject("fillet", "t5")) && t5v < 8000 && t5v > 7000, $"volume={t5v:F1} (cube 8000 minus fillets)");

// ── Task 6: "revolve a profile to make a hollow tube (washer)" (creation, revolve) ──
Fresh("t6");
tool.CreateBody("b6");
tool.Sketch(FreeCADTool.SketchAction.Create, "prof", "b6", "XZ_Plane");
tool.Sketch(FreeCADTool.SketchAction.Rectangle, "prof", properties: "{\"x\":5,\"y\":0,\"width\":10,\"height\":20}");
var rev = tool.Feature(FreeCADTool.FeatureAction.Revolve, "prof", "b6", "{\"angle\":360}");
var revInspect = tool.InspectObject("revolve", "t6");
double t6v = Vol(revInspect);
double t6exp = PI * (15 * 15 - 5 * 5) * 20; // 12566.37
Check("T6 revolved tube", NoErr(rev) && Math.Abs(t6v - t6exp) < 5, $"rev='{rev}' inspect={revInspect} volume={t6v:F1} expected≈{t6exp:F1}");

// ── Task 7: "fuse two 10mm cubes side by side into a 20x10x10 block" (creation, boolean) ──
Fresh("t7");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "a");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "b");
tool.Transform("b", position: "[10,0,0]");
var fuse7 = tool.Boolean(FreeCADTool.BooleanOperation.Fuse, "[\"a\",\"b\"]", "block", "t7");
var b7 = tool.InspectObject("block", "t7");
double t7v = Vol(b7);
Check("T7 fuse two cubes", NoErr(fuse7) && Math.Abs(t7v - 2000) < 1, $"fuse='{fuse7}' inspect={b7} volume={t7v:F1} expected=2000");

// ── Task 8: "export the bracket to STEP, then import it back" (roundtrip / editing pipeline) ──
var exp = tool.Export("step", "/t1_bracket.step", docName: "t1");
var imp = tool.ImportFile("/t1_bracket.step", "t2");
bool t8ok = NoErr(exp) && NoErr(imp) && File.Exists(Path.Combine(ws, "t1_bracket.step"));
Check("T8 export→import roundtrip", t8ok, $"export={exp} import={imp}");

// ── Task 9: "make the box longer, then undo" (editing; undo is GUI-only, so headless reports it) ──
Fresh("t9");
tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "bx", "t9");
tool.EditObject("bx", "{\"Length\":50}", "t9");
double edited = Vol(tool.InspectObject("bx", "t9"));
var u = tool.UndoRedo(FreeCADTool.UndoRedoAction.Undo, "t9");
Check("T9 edit works + undo reports GUI-required (headless)", Math.Abs(edited - 5000) < 1 && u.Contains("GUI"), $"edited={edited:F1} undo='{u}'");

// ── Task 10: "a polar pattern of 6 holes around a disc" (creation, pattern) ──
Fresh("t10");
tool.CreateBody("b10");
tool.Sketch(FreeCADTool.SketchAction.Create, "disc", "b10", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Circle, "disc", properties: "{\"cx\":0,\"cy\":0,\"radius\":20}");
tool.Feature(FreeCADTool.FeatureAction.Pad, "disc", "b10", "{\"length\":8}");
tool.Sketch(FreeCADTool.SketchAction.Create, "one", "b10", "XY_Plane");
tool.Sketch(FreeCADTool.SketchAction.Circle, "one", properties: "{\"cx\":12,\"cy\":0,\"radius\":2}");
tool.Feature(FreeCADTool.FeatureAction.Pocket, "one", "b10", "{\"length\":8,\"reversed\":true}");
var pol = tool.Pattern(FreeCADTool.PatternAction.Polar, "pocket", "b10", "{\"angle\":360,\"occurrences\":6}");
var p10 = tool.InspectObject("polar", "t10");
double t10v = Vol(p10);
double discv = PI * 20 * 20 * 8;
double holesv = 6 * PI * 2 * 2 * 8;
Check("T10 polar pattern 6 holes in disc", NoErr(pol) && Math.Abs(t10v - (discv - holesv)) < 5, $"pattern='{pol}' inspect={p10} volume={t10v:F1} expected≈{discv - holesv:F1}");

Console.WriteLine($"\n==== {pass} passed, {fail} failed ====");
Console.Out.Flush();
return fail == 0 ? 0 : 1;
