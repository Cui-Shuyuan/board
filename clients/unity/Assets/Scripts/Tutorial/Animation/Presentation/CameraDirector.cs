using UnityEngine;

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Applies a compiled camera frame.  It performs no framing geometry: the
    /// compiler owns center/ortho/pitch and writes them into *.compiled.json.
    /// </summary>
    public sealed class CameraDirector
    {
        public Camera Camera { get; private set; }
        public float GroundHalfWidth { get; private set; }

        public void EnsureCamera()
        {
            if (Camera != null) return;
            Camera = Camera.main;
            if (Camera == null)
            {
                var go = new GameObject("TutorialAnimCamera");
                go.tag = "MainCamera";
                Camera = go.AddComponent<Camera>();
            }
            Camera.orthographic = true;
            Camera.clearFlags = CameraClearFlags.SolidColor;
        }

        public void SetBackground(string hex)
        {
            EnsureCamera();
            var bg = new Color(0.12f, 0.13f, 0.16f, 1f);
            if (!string.IsNullOrEmpty(hex) && Palette.TryResolveRgb(hex, out var parsed)) bg = parsed;
            Camera.backgroundColor = bg;
            Camera.clearFlags = CameraClearFlags.SolidColor;
        }

        public void Apply(CompiledCameraDef frame, float fallbackAspect = 1.7778f)
        {
            EnsureCamera();
            if (frame == null) return;

            float pitch = frame.pitch > 0f ? frame.pitch : 90f;
            float aspect = Camera.aspect > 0.1f ? Camera.aspect : fallbackAspect;
            float ortho = Mathf.Max(0.01f, frame.ortho_size);
            float pitchRad = pitch * Mathf.Deg2Rad;
            float distance = ortho * 3.2f;
            var focus = new Vector3(frame.center_x, 0f, frame.center_z);
            var eye = focus + new Vector3(0f, Mathf.Sin(pitchRad), -Mathf.Cos(pitchRad)) * distance;

            Camera.orthographic = true;
            Camera.orthographicSize = ortho;
            Camera.transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, 0f, 0f));
            GroundHalfWidth = ortho * aspect;
        }
    }
}
