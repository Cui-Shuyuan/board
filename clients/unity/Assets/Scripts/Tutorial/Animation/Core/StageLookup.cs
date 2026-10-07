// Stage lookup helpers used by the pure timeline evaluator.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    public static class StageLookup
    {
        public static CompiledZoneDef Zone(CompiledStageDef stage, string zoneId)
        {
            if (stage?.zones == null || string.IsNullOrEmpty(zoneId)) return null;
            foreach (var z in stage.zones)
                if (z != null && z.zone == zoneId) return z;
            return null;
        }

        public static bool TrySlot(CompiledStageDef stage, string zoneId, int order, out float x, out float z)
        {
            x = z = 0f;
            var zone = Zone(stage, zoneId);
            if (zone?.slots == null) return false;
            foreach (var s in zone.slots)
                if (s != null && s.order == order)
                {
                    x = s.x;
                    z = s.z;
                    return true;
                }
            return false;
        }

        public static CompiledTemplateDef Template(CompiledStageDef stage, string templateId)
        {
            if (stage?.templates == null || string.IsNullOrEmpty(templateId)) return null;
            foreach (var t in stage.templates)
                if (t != null && t.id == templateId) return t;
            return null;
        }
    }
}
