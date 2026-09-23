using System.Globalization;
using System.Linq;
using System.Text.Json;
using AIOrchestrator;
using AIOrchestrator.API;

// Production simulation: ~100 realistic CAD design tasks that simulate an operator
// designing parts and assemblies, driven through the FreeCADTool method surface and
// instrumented to measure whether the read-only `analyze` capability
// (interfere / distance / centerOfMass) is NEEDED and ACTIONABLE in real design work —
// the keep-vs-remove evidence the methodology asks for.
//
// Categories:
//   SINGLE   single-part modeling (no measurement checkpoint expected)
//   FIT      part-in-part fit / clearance (analyze Interfere/Distance is the checkpoint)
//   POSITION placement / centroid / balance (analyze CenterOfMass/Distance)
//   VERIFY   explicit measurement query (analyze IS the task)
//
// NOTE: all numbers are interpolated into JSON with InvariantCulture so decimal
// values render as "3.3" not "3,3" (a comma breaks the JSON and the property is
// silently not applied). Object names are doc-scoped (each case has its own doc).

System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

var ws = Path.Combine(Path.GetTempPath(), "freecad_prod_" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(ws);
Setup.DocumentsPath = ws;
Console.WriteLine("Workspace: " + ws);

var tool = new FreeCADTool();
const double PI = Math.PI;
var CI = CultureInfo.InvariantCulture;

// Close every document left open by a prior run so names start clean.
{
    var lst = tool.Document(FreeCADTool.DocumentAction.List);
    int br = lst.IndexOf('[');
    if (br >= 0)
    {
        using var jd = JsonDocument.Parse(lst.Substring(br));
        foreach (var e in jd.RootElement.EnumerateArray())
            if (e.TryGetProperty("name", out var nm) && nm.GetString() is { } s)
            { try { tool.Document(FreeCADTool.DocumentAction.Close, s); } catch { } }
    }
}

bool NoErr(string s) => !s.StartsWith("Error:");
double Vol(string insp)
{
    if (insp.StartsWith("Error:")) return double.NaN;
    using var d = JsonDocument.Parse(insp);
    if (!d.RootElement.TryGetProperty("shape", out var sh) || sh.ValueKind == JsonValueKind.Null) return double.NaN;
    return sh.GetProperty("volume").GetDouble();
}
double ExtractDist(string r)
{
    if (r.StartsWith("Error:")) return double.NaN;
    int i = r.IndexOf(": ", StringComparison.Ordinal); if (i < 0) return double.NaN; i += 2;
    int j = r.IndexOf(" mm", i, StringComparison.Ordinal); if (j < 0) return double.NaN;
    return double.Parse(r.Substring(i, j - i), CI);
}
double[] ExtractVec(string r)
{
    if (r.StartsWith("Error:")) return Array.Empty<double>();
    int i = r.IndexOf('['); int j = r.IndexOf(']'); if (i < 0 || j < 0 || j <= i) return Array.Empty<double>();
    return r.Substring(i + 1, j - i - 1).Split(',').Select(s => double.Parse(s.Trim(), CI)).ToArray();
}

// tallies
int total = 0, nSingle = 0, nFit = 0, nPos = 0, nVerify = 0;
int needed = 0, called = 0, actionable = 0, correct = 0;
int docN = 0;
string NewDoc() => "p" + (++docN);

void Case(string cat, string desc, bool need, string anRes, bool act, bool ok, string detail = "")
{
    total++;
    if (cat == "SINGLE") nSingle++; else if (cat == "FIT") nFit++; else if (cat == "POSITION") nPos++; else nVerify++;
    if (need) needed++;
    if (anRes != "-") called++;
    if (act) actionable++;
    if (ok) correct++;
    Console.WriteLine($"[SIM] #{total,-3} {cat,-8} {(ok ? "OK" : "XX")} {desc}  {detail} | needed={need} analyze={anRes}");
}

string Fresh(string n) => tool.Document(FreeCADTool.DocumentAction.Create, n);
void Close(string n) => tool.Document(FreeCADTool.DocumentAction.Close, n);

// ── SINGLE: bracket WxHxT with 4 corner holes (PartDesign) ──
foreach (var (W, H, T) in new[] { (60.0, 40.0, 5.0), (80.0, 50.0, 6.0), (40.0, 40.0, 4.0), (100.0, 60.0, 8.0), (50.0, 30.0, 5.0), (70.0, 45.0, 6.0), (45.0, 45.0, 5.0), (90.0, 55.0, 7.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreateBody("b");
    tool.Sketch(FreeCADTool.SketchAction.Create, "sk", "b", "XY_Plane");
    tool.Sketch(FreeCADTool.SketchAction.Rectangle, "sk", properties: $"{{\"x\":0,\"y\":0,\"width\":{W},\"height\":{H}}}");
    tool.Feature(FreeCADTool.FeatureAction.Pad, "sk", "b", $"{{\"length\":{T}}}");
    tool.Sketch(FreeCADTool.SketchAction.Create, "hl", "b", "XY_Plane");
    foreach (var (hx, hy) in new[] { (10.0, 10.0), (W - 10, 10.0), (10.0, H - 10), (W - 10, H - 10) })
        tool.Sketch(FreeCADTool.SketchAction.Circle, "hl", properties: $"{{\"cx\":{hx},\"cy\":{hy},\"radius\":2.5}}");
    var pk = tool.Feature(FreeCADTool.FeatureAction.Pocket, "hl", "b", $"{{\"length\":{T},\"reversed\":true}}");
    double v = Vol(tool.InspectObject("pocket", d));
    double exp = W * H * T - 4 * PI * 2.5 * 2.5 * T;
    Case("SINGLE", $"bracket {W}x{H}x{T} + 4 corner holes", false, "-", false, NoErr(pk) && Math.Abs(v - exp) < 2, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── SINGLE: pipe OD/ID/L (boolean cut) ──
foreach (var (od, id, L) in new[] { (30.0, 20.0, 50.0), (40.0, 32.0, 80.0), (20.0, 12.0, 30.0), (50.0, 40.0, 100.0), (25.0, 15.0, 40.0), (36.0, 28.0, 60.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{od / 2},\"Height\":{L}}}", "outer", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{id / 2},\"Height\":{L + 10}}}", "bore", d);
    var cut = tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"outer\",\"bore\"]", "pipe", d);
    double v = Vol(tool.InspectObject("pipe", d));
    double exp = PI * ((od / 2) * (od / 2) - (id / 2) * (id / 2)) * L;
    Case("SINGLE", $"pipe OD{od}/ID{id}/L{L}", false, "-", false, NoErr(cut) && Math.Abs(v - exp) < 2, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── SINGLE: stepped shaft (two fused cylinders) ──
foreach (var (d1, l1, d2, l2) in new[] { (20.0, 30.0, 12.0, 20.0), (30.0, 40.0, 18.0, 25.0), (16.0, 25.0, 10.0, 15.0), (25.0, 35.0, 15.0, 30.0), (22.0, 28.0, 14.0, 22.0), (28.0, 45.0, 16.0, 18.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{d1 / 2},\"Height\":{l1}}}", "s1", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{d2 / 2},\"Height\":{l2}}}", "s2", d);
    tool.Transform("s2", position: $"[0,0,{l1}]");
    var f = tool.Boolean(FreeCADTool.BooleanOperation.Fuse, "[\"s1\",\"s2\"]", "shaft", d);
    double v = Vol(tool.InspectObject("shaft", d));
    double exp = PI * (d1 / 2) * (d1 / 2) * l1 + PI * (d2 / 2) * (d2 / 2) * l2;
    Case("SINGLE", $"stepped shaft {d1}x{l1}+{d2}x{l2}", false, "-", false, NoErr(f) && Math.Abs(v - exp) < 2, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── SINGLE: flange with bolt circle (polar pattern of holes) ──
foreach (var (D, t, bolts, bcDia, holeR) in new[] { (80.0, 10.0, 4, 60.0, 3.0), (100.0, 12.0, 6, 75.0, 3.5), (60.0, 8.0, 3, 45.0, 2.5), (120.0, 14.0, 8, 95.0, 4.0), (70.0, 10.0, 4, 52.0, 3.0), (90.0, 12.0, 6, 68.0, 3.5) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreateBody("b");
    tool.Sketch(FreeCADTool.SketchAction.Create, "disc", "b", "XY_Plane");
    tool.Sketch(FreeCADTool.SketchAction.Circle, "disc", properties: $"{{\"cx\":0,\"cy\":0,\"radius\":{D / 2}}}");
    tool.Feature(FreeCADTool.FeatureAction.Pad, "disc", "b", $"{{\"length\":{t}}}");
    tool.Sketch(FreeCADTool.SketchAction.Create, "one", "b", "XY_Plane");
    tool.Sketch(FreeCADTool.SketchAction.Circle, "one", properties: $"{{\"cx\":{bcDia / 2},\"cy\":0,\"radius\":{holeR}}}");
    tool.Feature(FreeCADTool.FeatureAction.Pocket, "one", "b", $"{{\"length\":{t},\"reversed\":true}}");
    var pol = tool.Pattern(FreeCADTool.PatternAction.Polar, "pocket", "b", $"{{\"angle\":360,\"occurrences\":{bolts}}}");
    double v = Vol(tool.InspectObject("polar", d));
    double exp = PI * (D / 2) * (D / 2) * t - bolts * PI * holeR * holeR * t;
    Case("SINGLE", $"flange D{D}/{bolts} bolts", false, "-", false, NoErr(pol) && Math.Abs(v - exp) < 5, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── SINGLE: enclosure box with wall (hollow shell via cut) ──
foreach (var (W, H, Dp, wall) in new[] { (60.0, 40.0, 30.0, 3.0), (80.0, 60.0, 40.0, 4.0), (50.0, 50.0, 25.0, 2.5), (100.0, 80.0, 50.0, 5.0), (70.0, 50.0, 35.0, 3.0), (40.0, 30.0, 20.0, 2.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, $"{{\"Length\":{W},\"Width\":{Dp},\"Height\":{H}}}", "shell", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, $"{{\"Length\":{W - 2 * wall},\"Width\":{Dp - 2 * wall},\"Height\":{H - wall}}}", "cavity", d);
    tool.Transform("cavity", position: $"[{wall},{wall},{wall}]");
    var c = tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"shell\",\"cavity\"]", "enclosure", d);
    double v = Vol(tool.InspectObject("enclosure", d));
    double exp = W * Dp * H - (W - 2 * wall) * (Dp - 2 * wall) * (H - wall);
    Case("SINGLE", $"enclosure {W}x{Dp}x{H} wall{wall}", false, "-", false, NoErr(c) && Math.Abs(v - exp) < 2, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── SINGLE: primitives (washer/cone/sphere/torus/disc) ──
foreach (var (kind, props, exp) in new[]
{
    ("washer", "{\"Radius\":15,\"Height\":3}", PI * 15 * 15 * 3),
    ("cone", "{\"Radius1\":10,\"Radius2\":3,\"Height\":20}", (PI * 20 / 3) * (100 + 9 + 30)),
    ("sphere", "{\"Radius\":12}", 4.0 / 3 * PI * 12 * 12 * 12),
    ("torus", "{\"Radius1\":15,\"Radius2\":4}", 2 * PI * PI * 15 * 4 * 4),
    ("disc", "{\"Radius\":25,\"Height\":2}", PI * 625 * 2),
})
{
    string d = NewDoc(); Fresh(d);
    var pk = kind switch { "cone" => FreeCADTool.PrimitiveKind.Cone, "sphere" => FreeCADTool.PrimitiveKind.Sphere, "torus" => FreeCADTool.PrimitiveKind.Torus, _ => FreeCADTool.PrimitiveKind.Cylinder };
    var cr = tool.CreatePrimitive(pk, props, "prim", d);
    double v = Vol(tool.InspectObject("prim", d));
    Case("SINGLE", $"primitive {kind}", false, "-", false, NoErr(cr) && Math.Abs(v - exp) / exp < 0.02, $"vol={v:F1} exp={exp:F1}");
    Close(d);
}

// ── FIT: pin in bore — housing = block CUT a bore cylinder (deterministic) ──
foreach (var (pinDia, boreDia) in new[] { (8.0, 10.0), (10.0, 10.0), (12.0, 10.0), (9.8, 10.0), (10.2, 10.0), (6.0, 10.0), (11.0, 10.0), (9.9, 10.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":30,\"Width\":30,\"Height\":15}", "blk", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{boreDia / 2},\"Height\":20}}", "borecyl", d);
    tool.Transform("borecyl", position: "[15,15,-2]");
    tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"blk\",\"borecyl\"]", "housing", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{pinDia / 2},\"Height\":15}}", "pin", d);
    tool.Transform("pin", position: "[15,15,0]");
    var interf = tool.Analyze(FreeCADTool.AnalysisKind.Interfere, "pin", "housing", d);
    bool collide = interf.Contains("INTERFERE");
    bool expect = pinDia > boreDia;
    Case("FIT", $"pin ⌀{pinDia} in bore ⌀{boreDia} → {(expect ? "WON'T fit" : "fits")}", true, interf.Replace("\n", " "), true, collide == expect, $"got={collide} exp={expect}");
    Close(d);
}

// ── FIT: bolt through clearance hole ──
foreach (var (bolt, hole) in new[] { (6.0, 6.6), (6.0, 6.0), (6.0, 5.8), (8.0, 8.4), (8.0, 7.9), (10.0, 10.5) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":24,\"Width\":24,\"Height\":8}", "blk", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{hole / 2},\"Height\":12}}", "holecyl", d);
    tool.Transform("holecyl", position: "[12,12,-2]");
    tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"blk\",\"holecyl\"]", "plate", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{bolt / 2},\"Height\":8}}", "bolt", d);
    tool.Transform("bolt", position: "[12,12,0]");
    var interf = tool.Analyze(FreeCADTool.AnalysisKind.Interfere, "bolt", "plate", d);
    bool collide = interf.Contains("INTERFERE");
    bool expect = bolt > hole;
    Case("FIT", $"bolt ⌀{bolt} through hole ⌀{hole} → {(expect ? "interference" : "clears")}", true, interf.Replace("\n", " "), true, collide == expect, $"got={collide} exp={expect}");
    Close(d);
}

// ── FIT: lid dropping into a box opening (frame = outer box CUT inner opening) ──
foreach (var lid in new[] { 28.0, 30.0, 32.0, 26.0, 31.0, 29.0 })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":40,\"Width\":40,\"Height\":20}", "obx", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":30,\"Width\":30,\"Height\":24}", "opening", d);
    tool.Transform("opening", position: "[5,5,-2]");
    tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"obx\",\"opening\"]", "frame", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, $"{{\"Length\":{lid},\"Width\":{lid},\"Height\":5}}", "lid", d);
    tool.Transform("lid", position: $"[{20 - lid / 2},{20 - lid / 2},8]");
    var interf = tool.Analyze(FreeCADTool.AnalysisKind.Interfere, "lid", "frame", d);
    bool collide = interf.Contains("INTERFERE");
    bool expect = lid > 30.0;
    Case("FIT", $"lid {lid} into 30 opening → {(expect ? "won't drop in" : "drops in")}", true, interf.Replace("\n", " "), true, collide == expect, $"got={collide} exp={expect}");
    Close(d);
}

// ── FIT: shaft in bearing bore ──
foreach (var (shaft, bore) in new[] { (15.0, 15.02), (15.0, 15.0), (15.0, 14.98), (20.0, 20.05), (20.0, 19.95) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":30,\"Width\":30,\"Height\":12}", "blk", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{bore / 2},\"Height\":16}}", "borecyl", d);
    tool.Transform("borecyl", position: "[15,15,-2]");
    tool.Boolean(FreeCADTool.BooleanOperation.Cut, "[\"blk\",\"borecyl\"]", "brg", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, $"{{\"Radius\":{shaft / 2},\"Height\":12}}", "shaft", d);
    tool.Transform("shaft", position: "[15,15,0]");
    var interf = tool.Analyze(FreeCADTool.AnalysisKind.Interfere, "shaft", "brg", d);
    bool collide = interf.Contains("INTERFERE");
    bool expect = shaft > bore;
    Case("FIT", $"shaft ⌀{shaft} in bearing ⌀{bore} → {(expect ? "too tight" : "seats")}", true, interf.Replace("\n", " "), true, collide == expect, $"got={collide} exp={expect}");
    Close(d);
}

// ── POSITION: centroid of an L-bracket (two fused boxes) ──
foreach (var (a, b, h) in new[] { (20.0, 20.0, 10.0), (30.0, 15.0, 12.0), (25.0, 25.0, 8.0), (40.0, 20.0, 10.0), (18.0, 28.0, 9.0), (22.0, 22.0, 11.0), (35.0, 15.0, 10.0), (28.0, 28.0, 7.0) })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, $"{{\"Length\":{a},\"Width\":{a / 2},\"Height\":{h}}}", "A", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, $"{{\"Length\":{a / 2},\"Width\":{b},\"Height\":{h}}}", "B", d);
    tool.Transform("B", position: $"[0,{a / 2},0]");
    tool.Boolean(FreeCADTool.BooleanOperation.Fuse, "[\"A\",\"B\"]", "L", d);
    var com = tool.Analyze(FreeCADTool.AnalysisKind.CenterOfMass, "L", null, d);
    double va = a * (a / 2) * h, vb = (a / 2) * b * h;
    double cx = (va * (a / 2) + vb * (a / 4)) / (va + vb);
    double cy = (va * (a / 4) + vb * (a / 2 + b / 2)) / (va + vb);
    double[] got = ExtractVec(com);
    bool ok = got.Length >= 2 && Math.Abs(got[0] - cx) < 0.1 && Math.Abs(got[1] - cy) < 0.1;
    Case("POSITION", $"L-bracket {a}/{b}x{h} centroid", true, com.Replace("\n", " "), true, ok, $"got=({(got.Length >= 2 ? got[0].ToString("F2", CI) : "?")},{(got.Length >= 2 ? got[1].ToString("F2", CI) : "?")}) exp=({cx:F2},{cy:F2})");
    Close(d);
}

// ── POSITION: distance from a part to a reference (alignment) ──
foreach (var gap in new[] { 2.0, 5.0, 10.0, 0.5, 15.0, 3.0, 8.0 })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "ref", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "mov", d);
    tool.Transform("mov", position: $"[{10 + gap},0,0]");
    var dist = tool.Analyze(FreeCADTool.AnalysisKind.Distance, "ref", "mov", d);
    double got = ExtractDist(dist);
    Case("POSITION", $"align part at {gap}mm gap", true, dist.Replace("\n", " "), true, Math.Abs(got - gap) < 0.05, $"got={got:F2} exp={gap:F2}");
    Close(d);
}

// ── VERIFY: explicit collision query (two parts overlapping or not) ──
foreach (var sep in new[] { 5.0, 8.0, 10.0, 12.0, 15.0, 9.0, 11.0, 20.0 })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "p1", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Box, "{\"Length\":10,\"Width\":10,\"Height\":10}", "p2", d);
    tool.Transform("p2", position: $"[{sep},0,0]");
    var interf = tool.Analyze(FreeCADTool.AnalysisKind.Interfere, "p1", "p2", d);
    bool collide = interf.Contains("INTERFERE");
    bool expect = sep < 10.0;
    Case("VERIFY", $"collision check sep={sep}mm → {(expect ? "collides" : "clear")}", true, interf.Replace("\n", " "), true, collide == expect, $"got={collide} exp={expect}");
    Close(d);
}

// ── VERIFY: explicit clearance query (gap between two cylinders) ──
foreach (var gap in new[] { 1.0, 2.5, 4.0, 7.0, 12.0, 0.2, 6.0 })
{
    string d = NewDoc(); Fresh(d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":5,\"Height\":10}", "c1", d);
    tool.CreatePrimitive(FreeCADTool.PrimitiveKind.Cylinder, "{\"Radius\":5,\"Height\":10}", "c2", d);
    tool.Transform("c2", position: $"[{10 + gap},0,0]");
    var dist = tool.Analyze(FreeCADTool.AnalysisKind.Distance, "c1", "c2", d);
    double got = ExtractDist(dist);
    Case("VERIFY", $"clearance query {gap}mm", true, dist.Replace("\n", " "), true, Math.Abs(got - gap) < 0.05, $"got={got:F2} exp={gap:F2}");
    Close(d);
}

// ── report ──
Console.WriteLine();
Console.WriteLine($"==== PRODUCTION SIM: {total} cases ====");
Console.WriteLine($"  SINGLE   = {nSingle}  (single-part modeling, no measurement)");
Console.WriteLine($"  FIT      = {nFit}  (part-in-part fit/clearance)");
Console.WriteLine($"  POSITION = {nPos}  (placement/centroid/balance)");
Console.WriteLine($"  VERIFY   = {nVerify}  (explicit measurement query)");
Console.WriteLine($"  measurement needed : {needed}/{total} ({100.0 * needed / total:F0}%)");
Console.WriteLine($"  analyze called     : {called}/{total} ({100.0 * called / total:F0}%)");
Console.WriteLine($"  analyze actionable : {actionable}/{total} ({100.0 * actionable / total:F0}%)");
Console.WriteLine($"  cases correct    : {correct}/{total} ({100.0 * correct / total:F0}%)");
Console.Out.Flush();
return correct == total ? 0 : 1;
