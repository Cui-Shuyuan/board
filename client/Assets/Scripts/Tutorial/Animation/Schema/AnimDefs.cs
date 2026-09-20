// v2 data schema definitions.  These are plain serializable data holders; the
// compiler is allowed to use them for normalized internal representation, while
// Unity runtime only ever loads the "compiled" subset.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public static class AnimSchemaV2
    {
        public const string Track = "tutorial-anim/v2";
        public const string Stage = "tutorial-stage/v2";
        public const string CompiledTrack = "tutorial-anim-compiled/v2";
        public const string CompiledStage = "tutorial-stage-compiled/v2";

        public static readonly string[] Transitions = { "continue", "overlay", "cut", "world_cut" };

        public static readonly string[] StateOps =
        {
            "ensure", "create", "destroy", "transfer", "stack", "shuffle", "move_order", "set_face"
        };

        public static readonly string[] PresentationOps =
        {
            "show", "highlight", "point", "fade", "scale", "wait"
        };

        public static bool IsStateOp(string op)
        {
            if (string.IsNullOrEmpty(op)) return false;
            foreach (var x in StateOps) if (x == op) return true;
            return false;
        }

        public static bool IsPresentationOp(string op)
        {
            if (string.IsNullOrEmpty(op)) return false;
            foreach (var x in PresentationOps) if (x == op) return true;
            return false;
        }

        public static bool IsKnownOp(string op) => IsStateOp(op) || IsPresentationOp(op);
    }

    [Serializable]
    public sealed class NamedText
    {
        public string key;
        public string text;
    }

    [Serializable]
    public sealed class WorldDef
    {
        public string id;
        public string mode;      // isolated | shared
        public string why;
    }

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
    public sealed class CameraDef
    {
        public List<string> zones = new List<string>();
        public float fill;
        public float at;         // cut cues must be 0.0
    }

    [Serializable]
    public sealed class ContractItemDef
    {
        public string template;
        public string palette;
        public int count = 1;
        public string face;      // up | down | hidden; empty = count only
    }

    [Serializable]
    public sealed class ContractZoneDef
    {
        public string zone;
        public List<ContractItemDef> items = new List<ContractItemDef>();
    }

    [Serializable]
    public sealed class ContractDef
    {
        public string picture;
        public List<ContractZoneDef> zones = new List<ContractZoneDef>();
    }

    [Serializable]
    public sealed class ScriptDef
    {
        public string story;
        public string narration;
        public string note;
        public CameraDef camera;
        public ContractDef enter;
        public ContractDef exit;
    }

    [Serializable]
    public sealed class EventDef
    {
        public string op;
        public float at;
        public float dur;
        public float lead;
        public string easing;
        public string realizes;

        // selector / state fields
        public string concept;
        public string template;
        public string palette;
        public List<PartRef> parts = new List<PartRef>();
        public string zone;
        public string source;
        public string destination;
        public int count = 1;
        public int quantity;
        public string to;        // face_up | face_down
        public int order = -1;
        public int slot = -1;

        // presentation fields
        public string part;
        public string indicator;
        public string picture;
        public float on = -1f;
        public float grow;
        public float peak_alpha = -1f;
        public float scale;
        public string scale_mode;
        public float to_alpha = -1f;
        public int capacity;
        public string real_templates;
        public string pad_template;
        public bool plain;
        public float stagger;
    }

    [Serializable]
    public sealed class CueDef
    {
        public string id;
        public string parent;
        public string tree;
        public string transition;
        public List<NamedText> timing = new List<NamedText>();
        public ScriptDef script;
        public List<EventDef> events = new List<EventDef>();
    }

    [Serializable]
    public sealed class TrackDef
    {
        public string schema;
        public string kind;
        public string game;
        public string track;
        public string default_tree;
        public List<WorldDef> worlds = new List<WorldDef>();
        public List<TreeDef> trees = new List<TreeDef>();
        public List<CueDef> cues = new List<CueDef>();
    }

    [Serializable]
    public sealed class SlotDef
    {
        public int order;
        public float x;
        public float z;
    }

    [Serializable]
    public sealed class CompiledZoneDef
    {
        public string zone;
        public List<SlotDef> slots = new List<SlotDef>();
    }

    [Serializable]
    public sealed class CompiledStageDef
    {
        public string schema;
        public string game;
        public string stage;
        public float pitch = 90f;
        public float aspect = 1.7778f;
        public List<CompiledZoneDef> zones = new List<CompiledZoneDef>();
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
        public string part;
        public string indicator;
        public string picture;
        public bool picture_on;
    }

    [Serializable]
    public sealed class CompiledCueDef
    {
        public string id;
        public string parent;
        public string tree;
        public string transition;
        public float duration;
        public CompiledCameraDef camera;
        public StateSnapshot start_state = new StateSnapshot();
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
        public List<CompiledCueDef> cues = new List<CompiledCueDef>();
    }
}
