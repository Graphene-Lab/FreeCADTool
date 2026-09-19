namespace AIOrchestrator.API;

// Enum members surface as the allowed values in the generated tool scheme and CLI docs, so they
// are intentionally self-describing without per-member XML summaries.
#pragma warning disable CS1591

/// <summary>Fixed value sets for FreeCADTool method parameters. Declared as enums (not strings)
/// so the allowed values are part of each method's signature and the generated tool scheme; the
/// dispatcher rejects any value outside the set before the method runs.</summary>
public partial class FreeCADTool
{
    /// <summary>Document lifecycle operations.</summary>
    public enum DocumentAction { List, Create, Open, Save, Close, Recompute }

    /// <summary>Undo/redo operations.</summary>
    public enum UndoRedoAction { Undo, Redo, Status }

    /// <summary>Sketch create/geometry operations.</summary>
    public enum SketchAction { Create, Rectangle, Circle, Line, Arc, Point }

    /// <summary>PartDesign feature operations.</summary>
    public enum FeatureAction { Pad, Pocket, Revolve, Groove, Hole, Loft, Sweep }

    /// <summary>Pattern operations.</summary>
    public enum PatternAction { Linear, Polar, Mirror }

    /// <summary>Edge operations.</summary>
    public enum EdgeOperation { Fillet, Chamfer }

    /// <summary>Primitive solid kinds.</summary>
    public enum PrimitiveKind { Box, Cylinder, Sphere, Cone, Torus, Wedge, Helix }

    /// <summary>3D view operations.</summary>
    public enum ViewAction { Screenshot, Angle, Fit, Zoom, Visibility, DisplayMode, Color, Render }

    /// <summary>Boolean solid operations.</summary>
    public enum BooleanOperation { Fuse, Cut, Common }
}

#pragma warning restore CS1591
