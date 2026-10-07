// Compiled/runtime animation data model loaded by Unity.
//
// The source animation schema is owned by the Python compiler and must not be
// duplicated here: only the compiled subset is part of the Unity contract.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    [Serializable]
    public sealed class TreeDef
    {
        public string id;
        public string world;
        public string stage;
        public string purpose;
        public string initial;
        public string extent_note;
    }

    [Serializable]
    public sealed class SlotDef
    {
        public int order;
        public float x;
        public float z;
    }


    [Serializable]
    public sealed class PaletteImageDef
    {
        public string palette;
        public string face_image;
        public string back_image;
    }

    [Serializable]
    public sealed class CompiledTemplateDef
    {
        public string id;
        public string shape;
        public string palette;
        public string face_image;
        public string back_image;
        public List<PaletteImageDef> face_image_by_palette = new List<PaletteImageDef>();
        public float width;
        public float height;
        public float world_size = 0.1f;
        public float alpha = 1f;
        public float rotation;
        public int sorting_order;
    }

    [Serializable]
    public sealed class CompiledZoneDef
    {
        public string zone;
        public string display;   // stage display mode ("stack" / "count" / "")
        public string role = "zone"; // "zone" | "offstage" (debug overlay skips offstage)
        public string label = "";    // human-readable zone name for the debug overlay
        public string group = "";    // optional player-area group, drawn as one larger box
        public string logical_zone = "";  // stage concept; empty for display-only zones
        public string logical_label = ""; // stage label used by QA summaries
        public List<PartRef> logical_parts = new List<PartRef>();
        public float min_x;
        public float max_x;
        public float min_z;
        public float max_z;
        public List<SlotDef> slots = new List<SlotDef>();
    }

    [Serializable]
    public sealed class CompiledGroupDef
    {
        public string group;
        public string label;
        public float min_x;
        public float max_x;
        public float min_z;
        public float max_z;
    }

    [Serializable]
    public sealed class CompiledOverlayDef
    {
        public string id;
        public string space; // screen | world
        public float x;
        public float y;
        public float z;
        public float w;
        public float h;
    }

    [Serializable]
    public sealed class CompiledStageDef
    {
        public string schema;
        public string game;
        public string stage;
        public float pitch = 90f;
        public float aspect = 1.7778f;
        public string background;
        public List<CompiledZoneDef> zones = new List<CompiledZoneDef>();
        public List<CompiledGroupDef> groups = new List<CompiledGroupDef>();
        public List<CompiledTemplateDef> templates = new List<CompiledTemplateDef>();
        public List<CompiledOverlayDef> overlays = new List<CompiledOverlayDef>();
    }

    [Serializable]
    public sealed class CompiledCameraDef
    {
        public float at;
        public float center_x;
        public float center_z;
        public float ortho_size;
        public float pitch = 90f;
        public float rect_min_x;
        public float rect_max_x;
        public float rect_min_z;
        public float rect_max_z;
    }

    [Serializable]
    public sealed class CompiledClipDef
    {
        public string kind;      // spawn | move | destroy | face | scale | fade | highlight | point | picture
        public string object_space = "entity"; // entity | screen
        public float at;
        public float dur;
        public float lead;
        public string easing;
        public string item_id;
        public string template;
        public string palette;
        public string from_zone;
        public int from_order = -1;
        public string to_zone;
        public int to_order = -1;
        public float from_x;
        public float from_z;
        public float to_x;
        public float to_z;
        public float from_scale = 1f;
        public float to_scale = 1f;
        public float from_alpha = 1f;
        public float to_alpha = 1f;
        public string to_face;
        public string from_face;
        public string flip_axis;
        public string flip_direction;
        public string flip_mode;
        public float flip_span;
        public int flip_side;
        public int from_layer;
        public int to_layer;
        public string part;
        public string indicator;
        public float marker_x;
        public float marker_z;
        public float marker_radius;
        public string overlay;
        public string text;
        public bool screen_space;
        public float label_x;
        public float label_y;
        public float label_w;
        public float label_h;

        // Unified annotation fields.  ``annotation_space`` is world (anchor
        // projected through the live camera) or screen (anchor fixed to a
        // viewport rect/overlay).  Emitted for point/shape/label/marker clips;
        // TimelineEvaluator applies its built-in defaults for absent values.
        public string annotation_space;
        public float part_u;
        public float part_v;
        public float part_w;
        public float part_h;
        public bool has_part_uv;
        public float nudge_x;
        public float nudge_y;
        // Optional annotation visual style, resolvable from the track-level
        // annotation_style + per-event style override in the source script.
        // Numeric values are pixels at a 1080p reference resolution.
        public string annotation_color;
        public float annotation_stroke;
        public float annotation_size;
        public float annotation_gap;
        public float screen_x;
        public float screen_y;
        public float screen_w;
        public float screen_h;
        public float world_x;
        public float world_z;

        // Magnifier lens: viewport rect plus the world-space region it shows.
        public float mag_x;
        public float mag_y;
        public float mag_w;
        public float mag_h;
        public float mag_center_x;
        public float mag_center_z;
        public float mag_ortho_size;
        public string mag_shape = "circle";
        public string mag_mask = "items";
        public string[] mag_item_ids;

        public string picture;
        public bool picture_on;

        // screen-space presentation overlay (does not create a ComponentState)
        public int layer;
        public string face_image;
        public string back_image;
        public string mask;
        public string background;
        public string source_item_id;
        public bool persist_on_source_missing = true;

        // Shuffle is visual-only: deterministic per-item jitter parameters
        // compiled so the runtime stays a pure function of the compiled asset.
        // Logical deck order is carried by state_ops/snapshots and is never
        // permuted by a shuffle event.
        public float sh_amp;
        public float sh_freq;
        public float sh_phase;
        public float sh_zamp;
        public float sh_env = 0.45f;
    }

    [Serializable]
    public sealed class CompiledStateOpDef
    {
        public string op;           // put | remove
        public float at;
        public string item_id;      // remove
        public ComponentState item; // put
    }

    [Serializable]
    public sealed class CompiledCameraOpDef
    {
        public float at;
        public float dur;
        public string easing;
        public string shot;
        public CompiledCameraDef frame;
    }

    [Serializable]
    public sealed class CompiledCueDef
    {
        public string id;
        public string parent;
        public string tree;
        public string transition;
        public string stage;
        public bool demo;
        public float duration;
        public CompiledCameraDef camera_in;
        public List<CompiledCameraOpDef> camera_ops = new List<CompiledCameraOpDef>();
        public List<CompiledStateOpDef> state_ops = new List<CompiledStateOpDef>();
        public StateSnapshot start_state = new StateSnapshot();
        public StateSnapshot first_state = new StateSnapshot();
        public StateSnapshot end_state = new StateSnapshot();
        public List<CompiledClipDef> clips = new List<CompiledClipDef>();
    }

    [Serializable]
    public sealed class CompiledTrackDef
    {
        public string schema;
        public string source_sha256;
        public string game;
        public string track;
        public List<TreeDef> trees = new List<TreeDef>();
        public List<CompiledStageDef> stages = new List<CompiledStageDef>();
        // zone_bindings is intentionally not modeled here.  Unity's JsonUtility
        // cannot deserialize a JSON object into a Dictionary; leaving the field
        // unknown is safe because Unity ignores it.  The Android QA layer and
        // editor tools read the top-level map directly.
        public List<CompiledCueDef> cues = new List<CompiledCueDef>();
    }
}
