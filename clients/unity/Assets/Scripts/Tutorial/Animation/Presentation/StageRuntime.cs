using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Read-only view over a compiled stage.  Slot coordinates are compiled data;
    /// this runtime never derives them from layout/capacity.
    /// </summary>
    public sealed class StageRuntime
    {
        public CompiledStageDef Stage { get; private set; }

        public float Pitch => Stage != null && Stage.pitch > 0f ? Stage.pitch : 90f;
        public float Aspect => Stage != null && Stage.aspect > 0f ? Stage.aspect : 1.7778f;

        public void Load(CompiledStageDef stage)
        {
            Stage = stage;
        }

        public bool TryTemplate(string id, out CompiledTemplateDef template)
        {
            template = StageLookup.Template(Stage, id);
            return template != null;
        }

        public bool TrySlot(string zoneId, int order, out float x, out float z)
        {
            return StageLookup.TrySlot(Stage, zoneId, order, out x, out z);
        }

        public Vector3 SlotPosition(string zoneId, int order)
        {
            if (TrySlot(zoneId, order, out float x, out float z)) return new Vector3(x, 0f, z);
            return Vector3.zero;
        }

        public Quaternion SpriteRotation(float roll)
        {
            return Quaternion.Euler(Pitch, 0f, 0f) * Quaternion.Euler(0f, 0f, roll);
        }
    }
}
