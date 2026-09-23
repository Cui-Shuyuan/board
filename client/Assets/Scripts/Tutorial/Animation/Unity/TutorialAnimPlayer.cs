using System.IO;
using UnityEngine;
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
        [Tooltip("Compiled track name under games/{game}/tutorial/anim/{track}.compiled.json")]
        public string track = "full";
        public bool animationEnabled = true;
        public bool runtimeTrace;

        [Header("Debug")]
        [Tooltip("调试模式：显示 zone 彩色范围框和左上角调试信息（运行时按 Z 切换）")]
        public bool debugZones;
        public KeyCode debugZonesKey = KeyCode.Z;

        public string CueId { get; private set; }
        public bool IsLoaded { get; private set; }
        public float TotalDuration => currentCue != null ? currentCue.duration : 0f;
        public CompiledCueDef CurrentCue => currentCue;
        public FrameState CurrentFrame { get; private set; }

        private readonly WorldRuntime runtime = new WorldRuntime();
        private readonly StageRuntime stageRuntime = new StageRuntime();
        private readonly SpriteLibrary sprites = new SpriteLibrary();
        private readonly CameraDirector cameraDirector = new CameraDirector();
        private readonly ActorBinder binder = new ActorBinder();
        private GameObject animRoot;
        private CompiledCueDef currentCue;
        private string gameRoot;
        private ZoneDebugOverlay zoneDebug;

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
            cameraDirector.Apply(frame.Camera, stageRuntime.Aspect);
            binder.Sync(frame);
            if (runtimeTrace) Debug.Log($"[TutorialAnimV2] {CueId} t={time:0.00} items={frame.Items.Count}");
        }

        public void Complete() => Seek(TotalDuration);

        public void ClearScene()
        {
            currentCue = null;
            CueId = null;
            CurrentFrame = null;
            binder.Clear();
            if (zoneDebug != null) zoneDebug.Clear();
        }

        public void RebuildZoneDebug()
        {
            if (zoneDebug == null) return;
            if (!debugZones)
            {
                zoneDebug.Clear();
                return;
            }
            zoneDebug.Sync(stageRuntime.Stage, cameraDirector.Camera);
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            bool toggleDebugZones = keyboard != null && keyboard.zKey.wasPressedThisFrame;
#else
            bool toggleDebugZones = Input.GetKeyDown(debugZonesKey);
#endif
            if (toggleDebugZones)
            {
                debugZones = !debugZones;
                RebuildZoneDebug();
            }
        }
    }
}
