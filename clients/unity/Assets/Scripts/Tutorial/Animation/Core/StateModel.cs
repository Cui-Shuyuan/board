// BoardGameTutorial.Animation v2 -- compiled state data model.
//
// The runtime view of a component is (zone, order, face).  Mutations are
// carried by compiled state_ops/snapshots; coordinates and tween calculations
// live in the compiler, never here.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public enum FaceState
    {
        Hidden = 0,
        Down = 1,
        Up = 2,
    }

    [Serializable]
    public sealed class PartRef
    {
        public string key;
        public string value;

        public PartRef() { }

        public PartRef(string key, string value)
        {
            this.key = key;
            this.value = value;
        }

        public PartRef Clone() => new PartRef(key, value);

        public override string ToString() => key + "=" + value;
    }

    [Serializable]
    public sealed class ComponentState
    {
        public string Id;
        public string TemplateId;
        public string Palette;
        public string Concept;
        public List<PartRef> parts = new List<PartRef>();
        public string ZoneId;
        public int Order;
        public int Layer;
        public FaceState Face = FaceState.Up;

        public ComponentState Clone()
        {
            var c = new ComponentState
            {
                Id = Id,
                TemplateId = TemplateId,
                Palette = Palette,
                Concept = Concept,
                ZoneId = ZoneId,
                Order = Order,
                Layer = Layer,
                Face = Face,
            };
            if (parts != null)
                foreach (var p in parts) c.parts.Add(p.Clone());
            return c;
        }

    }

    /// <summary>Full world snapshot.  Compiled cues use snapshots for jump/replay.</summary>
    [Serializable]
    public sealed class StateSnapshot
    {
        public List<ComponentState> components = new List<ComponentState>();
    }
}
