using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Runtime-only debug overlay: draws every on-stage zone as a colored rectangle
    /// and puts its label at the rectangle's top-left corner.
    /// Optional group boxes outline larger player areas.
    /// Toggle from TutorialAnimPlayer (default key Z).
    /// </summary>
    public sealed class ZoneDebugOverlay : MonoBehaviour
    {
        private const float BoxY = 0.03f;
        private const float LabelY = 0.045f;
        private readonly List<GameObject> parts = new List<GameObject>();
        private Material lineMaterial;

        public void Clear()
        {
            foreach (var go in parts)
                if (go != null) Destroy(go);
            parts.Clear();
            if (lineMaterial != null) Destroy(lineMaterial);
            lineMaterial = null;
        }

        public void Sync(CompiledStageDef stage, Camera camera)
        {
            Clear();
            if (stage == null || stage.zones == null || camera == null) return;

            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color")
                         ?? Shader.Find("Standard");
            if (shader != null) lineMaterial = new Material(shader);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            var labelRot = Quaternion.LookRotation(camera.transform.forward, camera.transform.up);

            var visible = new List<CompiledZoneDef>();
            foreach (var z in stage.zones)
            {
                if (z == null || string.IsNullOrEmpty(z.zone)) continue;
                if (z.role == "offstage") continue;
                if (z.max_x - z.min_x <= 0.0001f || z.max_z - z.min_z <= 0.0001f) continue;
                visible.Add(z);
            }

            DrawGroupBoxes(stage);

            var idx = 0;
            foreach (var z in visible)
            {
                var color = Color.HSVToRGB((idx * 0.6180339f) % 1f, 0.85f, 1f);
                idx++;

                DrawBox("ZoneBox_" + z.zone, z.min_x, z.max_x, z.min_z, z.max_z,
                        color, 0.016f, 2000);

                if (font == null) continue;
                DrawLabel("ZoneLabel_" + z.zone,
                          string.IsNullOrEmpty(z.label) ? z.zone : z.label,
                          z.min_x, z.max_z, color, labelRot, font, 0.035f, 2001);
            }
        }

        private void DrawGroupBoxes(CompiledStageDef stage)
        {
            if (stage.groups == null) return;
            foreach (var g in stage.groups)
            {
                if (g == null) continue;
                if (g.max_x - g.min_x <= 0.0001f || g.max_z - g.min_z <= 0.0001f) continue;
                DrawBox("ZoneGroup_" + g.group, g.min_x, g.max_x, g.min_z, g.max_z,
                        new Color(0.92f, 0.92f, 0.92f, 0.95f), 0.011f, 1990, BoxY - 0.006f);
            }
        }

        private void DrawBox(string name, float minX, float maxX, float minZ, float maxZ,
                             Color color, float width, int sortingOrder, float y = BoxY)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            parts.Add(go);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.positionCount = 4;
            lr.startWidth = lr.endWidth = width;
            lr.startColor = lr.endColor = color;
            lr.sortingOrder = sortingOrder;
            if (lineMaterial != null) lr.material = lineMaterial;
            lr.SetPositions(new[]
            {
                new Vector3(minX, y, minZ),
                new Vector3(maxX, y, minZ),
                new Vector3(maxX, y, maxZ),
                new Vector3(minX, y, maxZ),
            });
        }

        private void DrawLabel(string name, string text, float x, float z, Color color,
                               Quaternion rotation, Font font, float characterSize, int sortingOrder)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            parts.Add(go);
            var tm = go.AddComponent<TextMesh>();
            tm.font = font;
            tm.text = text;
            tm.fontSize = 48;
            tm.characterSize = characterSize;
            tm.anchor = TextAnchor.UpperLeft;
            tm.alignment = TextAlignment.Left;
            tm.color = color;
            go.transform.position = new Vector3(x, LabelY, z);
            go.transform.rotation = rotation;
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = sortingOrder;
        }
    }
}
