// Compiled timeline output types shared by evaluation and rendering.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Common target interface for every animated object, regardless of whether
    /// it lives in world space (entity/component) or screen space (overlay).
    /// Presentation primitives are written against this interface; the two
    /// concrete states below are the two implementations.
    /// </summary>
    public interface IAnimVisualObject
    {
        string ObjectId { get; }
        float Alpha { get; set; }
        float Scale { get; set; }
        bool Highlighted { get; set; }
        float HighlightGrow { get; set; }
        string PointPart { get; set; }
        string Indicator { get; set; }
    }

    public sealed class VisualItemState : IAnimVisualObject
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string ZoneId;
        public int Order;
        public int Layer;
        public float X;
        public float Z;
        public float Scale = 1f;
        // 1 = 无翻转；flip 动画中从 1 -> 0 -> 1，0 表示卡牌侧对镜头、正反都看不到。
        public float Flip = 1f;
        // long = hinge parallel to the long edge, collapse local X/width.
        // short = hinge parallel to the short edge, collapse local Y/height.
        public string FlipAxis = "long";
        public float Alpha = 1f;
        public float Rotation;
        public FaceState Face = FaceState.Up;
        public bool Visible = true;

        // Presentation-only modifiers, resolved by the binder.
        public bool Highlighted;
        public float HighlightGrow = 1f;
        public string PointPart;
        public string Indicator;

        string IAnimVisualObject.ObjectId { get { return Id; } }
        float IAnimVisualObject.Alpha { get { return Alpha; } set { Alpha = value; } }
        float IAnimVisualObject.Scale { get { return Scale; } set { Scale = value; } }
        bool IAnimVisualObject.Highlighted { get { return Highlighted; } set { Highlighted = value; } }
        float IAnimVisualObject.HighlightGrow { get { return HighlightGrow; } set { HighlightGrow = value; } }
        string IAnimVisualObject.PointPart { get { return PointPart; } set { PointPart = value; } }
        string IAnimVisualObject.Indicator { get { return Indicator; } set { Indicator = value; } }
    }

    /// <summary>
    /// A presentation annotation.  It is fully described by two explicit
    /// answers: *space* selects the coordinate system (world follows the
    /// camera / item; screen is fixed to the viewport), and *kind* selects the
    /// drawing primitive (arrow/circle/cross/forbid/box/label).
    ///
    /// World annotations below/right of a card are resolved to their current
    /// world position while evaluating the frame; screen annotations reference
    /// an overlay (or a baked viewport rect) and never touch the camera.
    /// </summary>
    public sealed class VisualAnnotationState
    {
        public string Space;      // world | screen
        public string Kind;       // arrow | circle | cross | forbid | box | label
        public string Text;       // label only
        public string ItemId;     // world anchor (optional)
        public string OverlayId;  // screen anchor (optional)
        public string Part;
        public float X;           // current world position for world
        public float Z;
        public float Radius = 0.2f;
        public float W;           // world box width; screen fallback width
        public float H;           // world box height; screen fallback height
        public float PartU = 0.5f;
        public float PartV = 0.5f;
        public float PartW;      // semantic sub-rect width, fraction of target rect
        public float PartH;      // semantic sub-rect height, fraction of target rect
        public float NudgeX;
        public float NudgeY;
        public string ColorHex;   // annotation style, from source script
        public float StrokePx;    // 1080p-reference pixels
        public float SizePx;      // 1080p-reference pixels, 0 = auto
        public float GapPx;       // 1080p-reference pixels, 0 = auto
        public float ScreenX;     // baked viewport fallback (screen)
        public float ScreenY;
        public float ScreenW;
        public float ScreenH;
        public float LabelW;
        public float LabelH;
    }

    /// <summary>
    /// Screen-space presentation overlay.  This is NOT a ComponentState: it
    /// never owns a physical card, never enters zone/order logic and is not
    /// counted by card-identity checks.  It is a camera/viewport-fixed asset
    /// reference, optionally linked to a source item for tooling.
    /// </summary>
    public sealed class VisualOverlayState : IAnimVisualObject
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string FaceImage;
        public string BackImage;
        public string Mask;
        public string Background;
        public float X;
        public float Y;
        public float W;
        public float H;
        public int Layer;
        public float Alpha = 1f;
        public float Scale = 1f;
        public bool Highlighted;
        public float HighlightGrow = 1f;
        public string PointPart;
        public string Indicator;
        public string SourceItemId;
        public bool PersistOnSourceMissing = true;

        string IAnimVisualObject.ObjectId { get { return Id; } }
        float IAnimVisualObject.Alpha { get { return Alpha; } set { Alpha = value; } }
        float IAnimVisualObject.Scale { get { return Scale; } set { Scale = value; } }
        bool IAnimVisualObject.Highlighted { get { return Highlighted; } set { Highlighted = value; } }
        float IAnimVisualObject.HighlightGrow { get { return HighlightGrow; } set { HighlightGrow = value; } }
        string IAnimVisualObject.PointPart { get { return PointPart; } set { PointPart = value; } }
        string IAnimVisualObject.Indicator { get { return Indicator; } set { Indicator = value; } }
    }

    /// <summary>
    /// A magnification lens: a viewport rect plus the world-space region that
    /// a dedicated camera renders into it each frame.  Because the lens shows
    /// the live world, highlighted items stay highlighted inside the lens and
    /// items flying out of the region also fly out of the lens.
    /// </summary>
    public sealed class VisualMagnifierState
    {
        public string Id;
        public string Shape = "circle";
        public string MaskMode = "items";
        public string[] ItemIds;
        public float X;
        public float Y;
        public float W;
        public float H;
        public float CenterX;
        public float CenterZ;
        public float OrthoSize;
        public int Layer;
        public float Alpha = 1f;
    }

    public sealed class FrameState
    {
        public string Picture;
        public CompiledCameraDef Camera;
        public readonly List<VisualItemState> Items = new List<VisualItemState>();
        public readonly List<VisualAnnotationState> Annotations = new List<VisualAnnotationState>();
        public readonly List<VisualOverlayState> Overlays = new List<VisualOverlayState>();
        public readonly List<VisualMagnifierState> Magnifiers = new List<VisualMagnifierState>();

        public VisualItemState Find(string id)
        {
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Id == id) return Items[i];
            return null;
        }
    }
}
