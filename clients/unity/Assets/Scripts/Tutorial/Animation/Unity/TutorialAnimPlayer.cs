using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial.Animation
{
    /// <summary>
    /// Thin Unity adapter for the v2 compiled animation runtime.
    ///
    /// It does exactly four jobs:
    ///   1. load the compiled track;
    ///   2. select a cue (all jump/replay state is already in the cue snapshot);
    ///   3. seek t and ask the pure TimelineEvaluator for a FrameState;
    ///   4. push that FrameState to ActorBinder and CameraDirector.
    ///
    /// No selector logic, no replay-entry-chain, no camera math, no coroutines.
    /// </summary>
    public sealed class TutorialAnimPlayer : MonoBehaviour
    {
        [Header("V2 animation")]
        [Tooltip("Compiled track name under content/games/{game}/tutorial/anim/{track}.compiled.json")]
        public string track = "full";
        public bool animationEnabled = true;
        public bool runtimeTrace;

        [Header("Debug")]
        [Tooltip("调试模式：显示 zone 彩色范围框和左上角调试信息（运行时按 Z 切换）")]
        public bool debugZones;
        public KeyCode debugZonesKey = KeyCode.Z;

        [Tooltip("是否允许调试开关。Android release 会由原生层显式关闭。")]
        public bool debugToggleEnabled = true;

        public string CueId { get; private set; }
        public bool IsLoaded { get; private set; }
        public float TotalDuration => currentCue != null ? currentCue.duration : 0f;
        public bool CurrentCueHoldAfterAudio => currentCue != null && currentCue.hold_after_audio;
        public float CurrentCuePauseAfterSpeechSeconds =>
            currentCue != null ? currentCue.pause_after_speech_seconds : 0f;
        public CompiledCueDef CurrentCue => currentCue;
        public FrameState CurrentFrame { get; private set; }
        public Camera Camera => cameraDirector.Camera;
        public bool FullScreenMaskActive => fullScreenMaskActive;

        private readonly WorldRuntime runtime = new WorldRuntime();
        private readonly StageRuntime stageRuntime = new StageRuntime();
        private readonly SpriteLibrary sprites = new SpriteLibrary();
        private readonly CameraDirector cameraDirector = new CameraDirector();
        private readonly ActorBinder binder = new ActorBinder();
        private GameObject animRoot;
        private CompiledCueDef currentCue;
        private string gameRoot;
        private ZoneDebugOverlay zoneDebug;
        private bool fullScreenMaskActive;
        private bool overlayDimActive;

        public bool LoadTrack(string gameRootPath, string trackName = null)
        {
            gameRoot = gameRootPath;
            string name = string.IsNullOrEmpty(trackName) ? track : trackName;
            string path = Path.Combine(gameRoot, "tutorial", "anim", "v2", name + ".compiled.json");
            if (!File.Exists(path))
                path = Path.Combine(gameRoot, "tutorial", "anim", name + ".compiled.json");
            if (!File.Exists(path))
            {
                Debug.LogError("[TutorialAnimV2] missing compiled track: " + path);
                IsLoaded = false;
                return false;
            }

            var doc = JsonUtility.FromJson<CompiledTrackDef>(File.ReadAllText(path));
            if (doc == null || doc.cues == null)
            {
                Debug.LogError("[TutorialAnimV2] failed to parse compiled track: " + path);
                IsLoaded = false;
                return false;
            }

            runtime.Load(doc);
            sprites.Init(gameRoot);
            cameraDirector.EnsureCamera();
            if (animRoot == null)
            {
                animRoot = new GameObject("TutorialAnimV2Root");
                animRoot.transform.SetParent(transform, false);
            }
            if (zoneDebug == null)
            {
                var debugGo = new GameObject("TutorialZoneDebug");
                debugGo.transform.SetParent(transform, false);
                zoneDebug = debugGo.AddComponent<ZoneDebugOverlay>();
            }
            binder.Init(animRoot.transform, stageRuntime, sprites, cameraDirector.Camera);
            IsLoaded = true;
            return true;
        }

        private sealed class SpritePreloadRequest
        {
            public UnityWebRequest Request;
            public string Relative;
            public string Shape;
        }

        /// <summary>
        /// Background preload for cutout images referenced by the compiled track.
        /// PNG decode/upload is done through UnityWebRequestTexture so later seeks
        /// do not have to synchronously decode all first-seen sprites.
        /// </summary>
        public IEnumerator PreloadCutoutSpritesRoutine()
        {
            if (!IsLoaded || runtime.Track == null || string.IsNullOrEmpty(gameRoot)) yield break;

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<KeyValuePair<string, string>>();
            void Add(string relative, string shape)
            {
                if (string.IsNullOrEmpty(relative)) return;
                if (!relative.EndsWith("_cutout.png", StringComparison.OrdinalIgnoreCase)) return;
                string key = relative + "|" + (shape ?? "");
                if (!wanted.Add(key)) return;
                ordered.Add(new KeyValuePair<string, string>(relative, shape ?? ""));
            }

            foreach (var stage in runtime.Track.stages ?? new List<CompiledStageDef>())
            {
                foreach (var tpl in stage.templates ?? new List<CompiledTemplateDef>())
                {
                    string shape = string.IsNullOrEmpty(tpl.shape) ? "card" : tpl.shape;
                    Add(tpl.face_image, shape);
                    Add(tpl.back_image, shape);
                    foreach (var p in tpl.face_image_by_palette ?? new List<PaletteImageDef>())
                    {
                        Add(p.face_image, shape);
                        Add(p.back_image, shape);
                    }
                }
            }
            foreach (var cue in runtime.Track.cues ?? new List<CompiledCueDef>())
            {
                foreach (var clip in cue.clips ?? new List<CompiledClipDef>())
                {
                    if (!string.IsNullOrEmpty(clip.picture)) Add(clip.picture, "card");
                }
            }

            int next = 0;
            const int concurrency = 4;
            var active = new List<SpritePreloadRequest>();
            while (next < ordered.Count || active.Count > 0)
            {
                while (active.Count < concurrency && next < ordered.Count)
                {
                    var pair = ordered[next++];
                    if (sprites.HasCached(pair.Key, pair.Value)) continue;
                    string fullPath = Path.IsPathRooted(pair.Key) ? pair.Key : Path.Combine(gameRoot, pair.Key);
                    if (!File.Exists(fullPath)) continue;
                    string uri;
                    try { uri = new Uri(fullPath).AbsoluteUri; }
                    catch { continue; }
                    var request = UnityWebRequestTexture.GetTexture(uri, false);
                    request.SendWebRequest();
                    active.Add(new SpritePreloadRequest
                    {
                        Request = request,
                        Relative = pair.Key,
                        Shape = pair.Value,
                    });
                }

                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var item = active[i];
                    if (item == null || item.Request == null || !item.Request.isDone) continue;
                    active.RemoveAt(i);
                    if (item.Request.result == UnityWebRequest.Result.Success)
                    {
                        var texture = DownloadHandlerTexture.GetContent(item.Request);
                        if (texture != null)
                        {
                            texture.wrapMode = TextureWrapMode.Clamp;
                            try { texture.Apply(true); } catch { /* already non-readable */ }
                            texture.filterMode = texture.mipmapCount > 1 ? FilterMode.Trilinear : FilterMode.Bilinear;
                            var sprite = Sprite.Create(texture,
                                new Rect(0f, 0f, texture.width, texture.height),
                                new Vector2(0.5f, 0.5f), 100f);
                            sprites.RegisterPreloaded(item.Relative, item.Shape, sprite);
                        }
                    }
                    item.Request.Dispose();
                }
                yield return null;
            }
        }

        /// <summary>
        /// Load the asset for a screen-space presentation overlay.  The overlay
        /// references a face image directly, so it does not need the template to
        /// exist in the current cue stage.
        /// </summary>
        public Sprite LoadOverlaySprite(VisualOverlayState overlay)
        {
            if (overlay == null || !IsLoaded) return null;
            string relative = !string.IsNullOrEmpty(overlay.FaceImage)
                ? overlay.FaceImage
                : overlay.BackImage;
            return string.IsNullOrEmpty(relative) ? null : sprites.LoadRelative(relative, "card");
        }

        public void BeginLensRender(string[] targetItemIds)
        {
            binder.BeginLensRender(targetItemIds);
        }

        public void EndLensRender()
        {
            binder.EndLensRender();
        }

        public bool LoadCue(string cueId)
        {
            if (!IsLoaded)
            {
                Debug.LogError("[TutorialAnimV2] LoadTrack first");
                return false;
            }
            if (!runtime.TryCue(cueId, out var cue))
            {
                Debug.LogError("[TutorialAnimV2] cue not in compiled track: " + cueId);
                return false;
            }

            currentCue = cue;
            CueId = cueId;
            stageRuntime.Load(runtime.StageForCue(cue));
            cameraDirector.SetBackground(stageRuntime.Stage != null ? stageRuntime.Stage.background : null);
            binder.Clear();
            Seek(0f);
            RebuildZoneDebug();
            return true;
        }

        public void Seek(float time)
        {
            if (!animationEnabled || currentCue == null) return;
            var frame = runtime.Evaluate(CueId, time);
            CurrentFrame = frame;
            ApplyFullScreenMask(frame);
            cameraDirector.Apply(frame.Camera, stageRuntime.Aspect);
            binder.Sync(frame);
            if (runtimeTrace) Debug.Log($"[TutorialAnimV2] {CueId} t={time:0.00} items={frame.Items.Count}");
        }

        private void ApplyFullScreenMask(FrameState frame)
        {
            VisualOverlayState mask = null;
            VisualOverlayState dim = null;
            if (frame != null && frame.Overlays != null)
            {
                foreach (var overlay in frame.Overlays)
                {
                    if (overlay == null) continue;
                    if (overlay.W < 0.999f || overlay.H < 0.999f) continue;
                    if (string.IsNullOrEmpty(overlay.Background)) continue;
                    if (overlay.DimAlpha > 0f)
                    {
                        // Dim overlay: keep the world visible at reduced
                        // brightness, but hide debug-zone geometry so title
                        // cards are never polluted by editor diagnostics.
                        if (dim == null) dim = overlay;
                        continue;
                    }
                    mask = overlay;
                    break;
                }
            }

            bool wasActive = fullScreenMaskActive;
            bool wasDim = overlayDimActive;
            // Update flags before RebuildZoneDebug(); otherwise an
            // active -> inactive transition can clear the zone layer forever.
            fullScreenMaskActive = mask != null;
            overlayDimActive = !fullScreenMaskActive && dim != null;

            if (fullScreenMaskActive)
            {
                var bg = Color.black;
                if (Palette.TryResolveRgb(mask.Background, out var parsed)) bg = parsed;
                // A full-screen mask owns the whole picture.  Hide the world
                // renderers and the zone debug layer so no world-space border
                // can leak through outside the OnGUI panel rect; keep the
                // camera clearing the full viewport with the mask color.
                cameraDirector.SetBackground(bg);
                if (animRoot != null) animRoot.SetActive(false);
                if (zoneDebug != null) zoneDebug.gameObject.SetActive(false);
            }
            else if (overlayDimActive)
            {
                // Keep the real scene and camera background, only suppress
                // zone debug drawing. The dim panel itself is rendered by
                // TutorialCuePlayer.DrawScreenOverlays at overlay.Alpha.
                if (animRoot != null) animRoot.SetActive(true);
                if (zoneDebug != null) zoneDebug.gameObject.SetActive(false);
                cameraDirector.SetBackground(stageRuntime.Stage != null ? stageRuntime.Stage.background : null);
            }
            else if (wasActive || wasDim)
            {
                if (animRoot != null) animRoot.SetActive(true);
                if (zoneDebug != null) zoneDebug.gameObject.SetActive(true);
                cameraDirector.SetBackground(stageRuntime.Stage != null ? stageRuntime.Stage.background : null);
                RebuildZoneDebug();
            }
        }

        public void Complete() => Seek(TotalDuration);

        public void ClearScene()
        {
            currentCue = null;
            CueId = null;
            CurrentFrame = null;
            if (fullScreenMaskActive || overlayDimActive)
            {
                fullScreenMaskActive = false;
                overlayDimActive = false;
                if (animRoot != null) animRoot.SetActive(true);
                if (zoneDebug != null) zoneDebug.gameObject.SetActive(true);
                cameraDirector.SetBackground(stageRuntime.Stage != null ? stageRuntime.Stage.background : null);
            }
            binder.Clear();
            if (zoneDebug != null) zoneDebug.Clear();
        }

        public void RebuildZoneDebug()
        {
            if (zoneDebug == null) return;
            if (!debugZones || fullScreenMaskActive || overlayDimActive)
            {
                zoneDebug.Clear();
                return;
            }
            zoneDebug.Sync(stageRuntime.Stage, cameraDirector.Camera);
        }

        public void SetDebugOverlay(bool enabled)
        {
            debugZones = enabled && debugToggleEnabled;
            RebuildZoneDebug();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            bool toggleDebugZones = keyboard != null && keyboard.zKey.wasPressedThisFrame;
#else
            bool toggleDebugZones = Input.GetKeyDown(debugZonesKey);
#endif
            if (toggleDebugZones && debugToggleEnabled)
            {
                debugZones = !debugZones;
                RebuildZoneDebug();
            }
        }
    }
}
