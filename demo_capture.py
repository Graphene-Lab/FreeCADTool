import os, math
import FreeCAD as App
import Part
from FreeCAD import Vector

# Self-contained demo capture: builds a complex turbine impeller and renders an
# orbiting sequence of frames for a README GIF. Run with: freecad.exe demo_capture.py
outdir = os.environ.get("CAPTURE_OUT", os.path.join(os.path.expanduser("~"), "freecad_frames"))
os.makedirs(outdir, exist_ok=True)

doc = App.newDocument("demo")

# --- complex solid: turbine impeller ---
hub    = Part.makeCylinder(18, 40)
cap    = Part.makeCone(18, 6, 14, Vector(0, 0, 40))
flange = Part.makeCylinder(30, 6)
shroud = Part.makeCylinder(46, 36).cut(Part.makeCylinder(42, 36))

solid = hub.fuse(cap).fuse(flange).fuse(shroud)
for i in range(12):
    blade = Part.makeBox(38, 3, 30, Vector(8, -1.5, 6))
    blade.rotate(Vector(0, 0, 0), Vector(0, 0, 1), i * 30)
    solid = solid.fuse(blade)
# central bore + bolt circle
solid = solid.cut(Part.makeCylinder(8, 100, Vector(0, 0, -10)))
for i in range(6):
    a = math.radians(i * 60)
    bolt = Part.makeCylinder(3, 20, Vector(24 * math.cos(a), 24 * math.sin(a), -5))
    solid = solid.cut(bolt)

solid = solid.removeSplitter()
feat = doc.addObject("Part::Feature", "Impeller")
feat.Shape = solid
doc.recompute()

# --- orbiting capture ---
import FreeCADGui as Gui
view = Gui.ActiveDocument.ActiveView
view.viewIsometric()
view.fitAll()
Gui.updateGui()

N = 36
for i in range(N):
    ang = math.radians(i * 360.0 / N)
    dx = math.sin(ang)
    dy = -math.cos(ang)
    view.setViewDirection((dx, dy, -0.45))
    view.fitAll()
    Gui.updateGui()
    view.saveImage(os.path.join(outdir, "frame_%02d.png" % i), 900, 700, 'White')
    Gui.updateGui()

print("CAPTURE_DONE frames=%d out=%s" % (N, outdir), flush=True)
