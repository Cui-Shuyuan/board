// 数据驱动的教学动画播放器。
//
// 职责：
// 1. 从 StreamingAssets 或 persistentDataPath 读取 tutorial.json；
// 2. 根据 board/slots/sprites 数据搭景（无场景依赖，空场景即可运行）；
// 3. 顺序播放章节时间轴，只调用 TutorialPrimitives 里的原语；
// 4. 播放过程只读数据，不调用任何 LLM。
//
// 注意：这是第一版运行时骨架，相机取景、字幕样式、高亮外观等视觉参数需要
// 在 Unity 里按实际素材再微调。数据契约以 tutorial/schema/tutorial.schema.json 为准。
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial
{
    public class TutorialDirector : MonoBehaviour
    {
        [Header("Runtime config")]
        [Tooltip("game_id，决定读取哪个游戏的教程数据")]
        public string gameId = "splendor";

        [Tooltip("tutorial.json 所在根目录。Windows 编辑器下可直接指向仓库 games 目录；安卓上由 App 下载后填写。")]
        public string tutorialRoot = "";

        [Tooltip("是否在场景加载后自动开始播放")]
        public bool autoPlay = true;

        [Tooltip("播放速度倍率，便于调试")]
        public float timeScale = 1f;

        private TutorialDoc doc;
        private readonly Dictionary<string, TutorialSlot> slotsById = new Dictionary<string, TutorialSlot>();
        private readonly Dictionary<string, TutorialSprite> spritesById = new Dictionary<string, TutorialSprite>();
        private readonly Dictionary<string, GameObject> slotObjects = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, GameObject> spriteObjects = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Vector3> spriteBaseScales = new Dictionary<string, Vector3>();
        private readonly Dictionary<string, GameObject> highlightObjects = new Dictionary<string, GameObject>();

        private GameObject boardObject;
        private Coroutine playRoutine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // 如果场景里已经手动挂了 TutorialDirector，就不用再建。
            if (UnityEngine.Object.FindFirstObjectByType<TutorialDirector>() != null) return;
            var go = new GameObject("TutorialDirector");
            go.AddComponent<TutorialDirector>();
        }

        private void Start()
        {
            if (autoPlay) LoadAndPlay();
        }

        public void LoadAndPlay()
        {
            LoadTutorial(gameId);
            if (doc != null)
            {
                playRoutine = StartCoroutine(PlayAllChapters());
            }
        }

        public void Stop()
        {
            if (playRoutine != null)
            {
                StopCoroutine(playRoutine);
                playRoutine = null;
            }
        }

        /// <summary>
        /// 读取并解析 tutorial.json，然后搭景。
        /// 根目录优先级：tutorialRoot 参数 > persistentDataPath/{gameId} > streamingAssetsPath/{gameId}。
        /// </summary>
        public bool LoadTutorial(string gameId)
        {
            this.gameId = gameId;
            string root = ResolveTutorialRoot(gameId);
            if (string.IsNullOrEmpty(root))
            {
                Debug.LogError($"[TutorialDirector] tutorial root not found for game '{gameId}'. Set tutorialRoot or put files under streamingAssetsPath/{gameId}.");
                return false;
            }

            string jsonPath = Path.Combine(root, "tutorial.json");
            if (!File.Exists(jsonPath))
            {
                Debug.LogError($"[TutorialDirector] missing {jsonPath}");
                return false;
            }

            string json = File.ReadAllText(jsonPath);
            doc = JsonUtility.FromJson<TutorialDoc>(json);
            if (doc == null || doc.meta == null)
            {
                Debug.LogError($"[TutorialDirector] failed to parse {jsonPath}");
                return false;
            }

            NormalizeDefaults();
            BuildScene(root);
            return true;
        }

        private string ResolveTutorialRoot(string gameId)
        {
            if (!string.IsNullOrEmpty(tutorialRoot))
            {
                string r = Path.Combine(tutorialRoot, gameId);
                if (Directory.Exists(r)) return r;
                Debug.LogWarning($"[TutorialDirector] tutorialRoot '{tutorialRoot}' does not contain {gameId}");
            }

            string p = Path.Combine(Application.persistentDataPath, gameId);
            if (Directory.Exists(p)) return p;

            string s = Path.Combine(Application.streamingAssetsPath, gameId);
            if (Directory.Exists(s)) return s;

            return null;
        }

        /// <summary>
        /// JsonUtility 不会套用字段初始化值，schema 里声明的 default 在这里补齐。
        /// </summary>
        private void NormalizeDefaults()
        {
            if (doc.board != null)
            {
                if (string.IsNullOrEmpty(doc.board.origin)) doc.board.origin = "top_left";
                if (doc.board.pixels_per_unit <= 0f) doc.board.pixels_per_unit = 100f;
            }

            if (doc.meta != null)
            {
                if (string.IsNullOrEmpty(doc.meta.default_easing)) doc.meta.default_easing = Easing.Default;
                if (string.IsNullOrEmpty(doc.meta.default_highlight_color)) doc.meta.default_highlight_color = "#FFD54F";
            }

            if (doc.slots != null)
            {
                foreach (var slot in doc.slots)
                {
                    if (string.IsNullOrEmpty(slot.id)) continue;
                    slotsById[slot.id] = slot;
                }
            }

            if (doc.sprites != null)
            {
                foreach (var sprite in doc.sprites)
                {
                    if (string.IsNullOrEmpty(sprite.id)) continue;
                    if (string.IsNullOrEmpty(sprite.pivot)) sprite.pivot = "center";
                    spritesById[sprite.id] = sprite;
                }
            }
        }

        // ── 场景搭建 ──────────────────────────────────────────────────────
        private void BuildScene(string root)
        {
            EnsureCamera();

            // 清掉旧场景对象（重复加载时）。
            ClearDynamicObjects();

            var rootGo = new GameObject("TutorialRoot");
            rootGo.transform.SetParent(transform, false);

            BuildBoard(root, rootGo);
            BuildSlots(rootGo);
            BuildSprites(root, rootGo);
            BuildHighlights(rootGo);
        }

        private void ClearDynamicObjects()
        {
            // Board / slots / sprites / highlights 都挂在 TutorialRoot 下。
            Transform old = transform.Find("TutorialRoot");
            if (old != null) UnityEngine.Object.Destroy(old.gameObject);

            slotObjects.Clear();
            spriteObjects.Clear();
            spriteBaseScales.Clear();
            highlightObjects.Clear();
            boardObject = null;
        }

        private void EnsureCamera()
        {
            if (Camera.main != null) return;

            var camGo = new GameObject("TutorialCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.16f, 0.20f, 1f);

            float h = doc.board != null && doc.board.height_mm > 0f ? doc.board.height_mm : 260f;
            cam.orthographicSize = h * 0.75f;
            camGo.transform.position = new Vector3(0f, h * 0.9f, -h * 0.75f);
            camGo.transform.LookAt(new Vector3(0f, 0f, 0f));
        }

        private void BuildBoard(string root, GameObject parent)
        {
            if (doc.board == null || string.IsNullOrEmpty(doc.board.image)) return;

            Sprite sprite = LoadSpriteFromFile(Path.Combine(root, "media", doc.board.image), doc.board.pixels_per_unit, new Vector2(0.5f, 0.5f));
            if (sprite == null) return;

            boardObject = new GameObject("Board");
            boardObject.transform.SetParent(parent.transform, false);
            var sr = boardObject.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = -10;

            float defaultW = sprite.rect.width / doc.board.pixels_per_unit;
            float defaultH = sprite.rect.height / doc.board.pixels_per_unit;
            float scaleX = doc.board.width_mm / defaultW;
            float scaleY = doc.board.height_mm / defaultH;
            boardObject.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            boardObject.transform.localScale = new Vector3(scaleX, scaleY, 1f);
        }

        private void BuildSlots(GameObject parent)
        {
            if (doc.slots == null) return;

            foreach (var slot in doc.slots)
            {
                var go = new GameObject("slot:" + slot.id);
                go.transform.SetParent(parent.transform, false);
                go.transform.position = SlotToWorld(slot);
                slotObjects[slot.id] = go;
            }
        }

        private void BuildSprites(string root, GameObject parent)
        {
            if (doc.sprites == null) return;

            foreach (var def in doc.sprites)
            {
                Sprite sprite = LoadSpriteFromFile(Path.Combine(root, "media", def.file), doc.board != null ? doc.board.pixels_per_unit : 100f, PivotOf(def.pivot));
                if (sprite == null)
                {
                    Debug.LogWarning($"[TutorialDirector] sprite not found: {def.file}");
                    continue;
                }

                var go = new GameObject("sprite:" + def.id);
                go.transform.SetParent(parent.transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = sprite;
                sr.sortingOrder = 1;

                float defaultW = sprite.rect.width / (doc.board != null && doc.board.pixels_per_unit > 0f ? doc.board.pixels_per_unit : 100f);
                float defaultH = sprite.rect.height / (doc.board != null && doc.board.pixels_per_unit > 0f ? doc.board.pixels_per_unit : 100f);
                Vector3 baseScale = new Vector3(
                    def.width_mm / defaultW,
                    def.height_mm / defaultH,
                    1f
                );

                go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                go.transform.localScale = baseScale;
                spriteBaseScales[def.id] = baseScale;
                spriteObjects[def.id] = go;

                go.transform.position = FindInitialSpawnPosition(def.id);
            }
        }

        private void BuildHighlights(GameObject parent)
        {
            // 每个 slot 和 sprite 都预建一个高亮圆盘，默认关闭。
            var ids = new List<string>();
            if (doc.slots != null) foreach (var s in doc.slots) ids.Add(s.id);
            foreach (var id in ids)
            {
                if (!slotObjects.TryGetValue(id, out var target)) continue;
                var hl = CreateHighlightDisc(id, parent);
                hl.transform.SetParent(target.transform, false);
                hl.transform.localPosition = new Vector3(0f, 0.02f, 0f);
                highlightObjects[id] = hl;
            }

            var spriteIds = new List<string>(spriteObjects.Keys);
            foreach (var id in spriteIds)
            {
                var target = spriteObjects[id];
                var hl = CreateHighlightDisc(id, parent);
                hl.transform.SetParent(target.transform, false);
                hl.transform.localPosition = new Vector3(0f, 0.02f, 0f);
                hl.name = "highlight:" + id;
                highlightObjects["sprite:" + id] = hl;
            }
        }

        private GameObject CreateHighlightDisc(string ownerId, GameObject parent)
        {
            var go = new GameObject("highlight:" + ownerId);
            go.transform.SetParent(parent.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GenerateSoftDiscSprite(128, new Color(1f, 1f, 1f, 1f));
            sr.color = ColorFromHex(doc.meta != null ? doc.meta.default_highlight_color : "#FFD54F");
            sr.sortingOrder = 5;
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            go.transform.localScale = Vector3.one * 0.6f;
            go.SetActive(false);
            return go;
        }

        private Sprite GenerateSoftDiscSprite(int size, Color color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float r = size * 0.48f;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), center);
                    float a = Mathf.Clamp01(1f - d / r);
                    a = a * a * (3f - 2f * a); // smoothstep 边缘
                    pixels[y * size + x] = new Color(color.r, color.g, color.b, a);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Color ColorFromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new Color(1f, 0.84f, 0.31f, 1f);
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length < 6) return new Color(1f, 0.84f, 0.31f, 1f);
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }

        private Sprite LoadSpriteFromFile(string path, float pixelsPerUnit, Vector2 pivot)
        {
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[TutorialDirector] file not found: {path}");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[TutorialDirector] failed to load image: {path}");
                return null;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), pivot, pixelsPerUnit);
        }

        private static Vector2 PivotOf(string pivot)
        {
            switch (pivot)
            {
                case "top_left": return new Vector2(0f, 1f);
                case "bottom_center": return new Vector2(0.5f, 0f);
                default: return new Vector2(0.5f, 0.5f);
            }
        }

        // ── 坐标换算 ──────────────────────────────────────────────────────
        private Vector3 SlotToWorld(TutorialSlot slot)
        {
            float w = doc.board != null ? doc.board.width_mm : 0f;
            float h = doc.board != null ? doc.board.height_mm : 0f;
            float x, z;
            string origin = doc.board != null && !string.IsNullOrEmpty(doc.board.origin) ? doc.board.origin : "top_left";

            if (origin == "center")
            {
                x = slot.x * w;
                z = -slot.y * h;
            }
            else
            {
                x = (slot.x - 0.5f) * w;
                z = (0.5f - slot.y) * h;
            }
            return new Vector3(x, slot.z, z);
        }

        private Vector3 ResolvePosition(TutorialPosition p, string slotId, Vector3 fallback)
        {
            if (p != null) return new Vector3(p.x, p.z, p.y); // JSON 的 x/y 是版图平面坐标，y 映射到世界 Z
            if (!string.IsNullOrEmpty(slotId) && slotObjects.TryGetValue(slotId, out var go)) return go.transform.position;
            return fallback;
        }

        private Vector3 FindInitialSpawnPosition(string spriteId)
        {
            if (doc.chapters != null)
            {
                foreach (var ch in doc.chapters)
                {
                    if (ch.timeline == null) continue;
                    foreach (var ev in ch.timeline)
                    {
                        if (ev.sprite == spriteId && !string.IsNullOrEmpty(ev.from_slot) && slotObjects.TryGetValue(ev.from_slot, out var slotGo))
                        {
                            return slotGo.transform.position;
                        }
                    }
                }
            }
            return Vector3.zero;
        }

        // ── 播放控制 ──────────────────────────────────────────────────────
        private IEnumerator PlayAllChapters()
        {
            if (doc == null || doc.chapters == null) yield break;

            for (int i = 0; i < doc.chapters.Length; i++)
            {
                yield return PlayChapter(doc.chapters[i]);
            }
            Debug.Log("[TutorialDirector] all chapters finished.");
        }

        public IEnumerator PlayChapter(TutorialChapter chapter)
        {
            if (chapter == null || chapter.timeline == null || chapter.timeline.Length == 0)
            {
                Debug.Log($"[TutorialDirector] chapter '{chapter?.id}' has no timeline.");
                yield break;
            }

            Debug.Log($"[TutorialDirector] start chapter '{chapter.id}'");
            float chapterTime = 0f;

            foreach (var ev in chapter.timeline)
            {
                while (chapterTime < ev.t)
                {
                    chapterTime += Time.deltaTime * timeScale;
                    yield return null;
                }

                yield return StartCoroutine(RunEvent(ev));
            }

            Debug.Log($"[TutorialDirector] chapter '{chapter.id}' finished.");
        }

        private IEnumerator RunEvent(TutorialEvent ev)
        {
            float duration = Mathf.Max(0f, ev.duration);
            string easing = string.IsNullOrEmpty(ev.easing) ? (doc.meta != null ? doc.meta.default_easing : Easing.Default) : ev.easing;

            switch (ev.action)
            {
                case "move":
                {
                    if (!spriteObjects.TryGetValue(ev.sprite, out var go)) yield break;
                    Transform target = go.transform;
                    Vector3 from = ResolvePosition(ev.from_position, ev.from_slot, target.position);
                    Vector3 to = ResolvePosition(ev.to_position, ev.to_slot, target.position);
                    yield return StartCoroutine(TutorialPrimitives.TweenPosition(target, from, to, duration, easing));
                    break;
                }
                case "flip":
                {
                    if (!spriteObjects.TryGetValue(ev.sprite, out var go)) yield break;
                    float fromYaw = go.transform.localEulerAngles.y;
                    yield return StartCoroutine(TutorialPrimitives.TweenFlip(go.transform, fromYaw, duration, easing));
                    break;
                }
                case "rotate":
                {
                    if (!spriteObjects.TryGetValue(ev.sprite, out var go)) yield break;
                    float fromYaw = go.transform.localEulerAngles.y;
                    yield return StartCoroutine(TutorialPrimitives.TweenYaw(go.transform, fromYaw, ev.rotation, duration, easing));
                    break;
                }
                case "scale":
                {
                    if (!spriteObjects.TryGetValue(ev.sprite, out var go)) yield break;
                    if (!spriteBaseScales.TryGetValue(ev.sprite, out var baseScale)) baseScale = go.transform.localScale;
                    Vector3 from = go.transform.localScale;
                    Vector3 to = baseScale * ev.scale;
                    yield return StartCoroutine(TutorialPrimitives.TweenScale(go.transform, from, to, duration, easing));
                    break;
                }
                case "fade":
                {
                    if (!spriteObjects.TryGetValue(ev.sprite, out var go)) yield break;
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr == null) yield break;
                    yield return StartCoroutine(TutorialPrimitives.TweenAlpha(sr, sr.color.a, ev.opacity, duration, easing));
                    break;
                }
                case "highlight":
                {
                    var hl = FindHighlight(ev.slot, ev.sprite);
                    if (hl == null) yield break;
                    yield return StartCoroutine(HighlightPulse(hl, ev.color, duration, ev.loop));
                    break;
                }
                case "shuffle":
                {
                    if (ev.sprites != null && ev.sprites.Length >= 2)
                    {
                        yield return StartCoroutine(ShuffleSprites(ev.sprites, duration, easing));
                    }
                    else if (!string.IsNullOrEmpty(ev.sprite) && spriteObjects.TryGetValue(ev.sprite, out var deckGo))
                    {
                        yield return StartCoroutine(TutorialPrimitives.TweenShuffleInPlace(deckGo.transform, duration, easing));
                    }
                    break;
                }
                case "wait":
                {
                    if (duration > 0f) yield return new WaitForSeconds(duration / timeScale);
                    break;
                }
                default:
                    Debug.LogWarning($"[TutorialDirector] unknown action '{ev.action}'");
                    yield break;
            }
        }

        private GameObject FindHighlight(string slotId, string spriteId)
        {
            if (!string.IsNullOrEmpty(slotId) && highlightObjects.TryGetValue(slotId, out var slotHl)) return slotHl;
            if (!string.IsNullOrEmpty(spriteId) && highlightObjects.TryGetValue("sprite:" + spriteId, out var spriteHl)) return spriteHl;
            return null;
        }

        private IEnumerator HighlightPulse(GameObject hl, string colorHex, float duration, bool loop)
        {
            hl.SetActive(true);
            var sr = hl.GetComponent<SpriteRenderer>();
            Color target = ColorFromHex(string.IsNullOrEmpty(colorHex) ? (doc.meta != null ? doc.meta.default_highlight_color : "#FFD54F") : colorHex);
            Color original = sr != null ? sr.color : target;

            float t = 0f;
            do
            {
                t = 0f;
                while (t < duration)
                {
                    t += Time.deltaTime * timeScale;
                    float k = 0.5f - 0.5f * Mathf.Cos((t / duration) * Mathf.PI * 2f);
                    if (sr != null)
                    {
                        sr.color = Color.Lerp(original, target, k);
                    }
                    float s = 0.6f + 0.25f * k;
                    hl.transform.localScale = new Vector3(s, s, 1f);
                    yield return null;
                }
            } while (loop && duration > 0f);

            hl.SetActive(false);
        }

        private IEnumerator ShuffleSprites(string[] spriteIds, float duration, string easing)
        {
            if (spriteIds == null || spriteIds.Length < 2) yield break;

            var targets = new List<Transform>();
            var positions = new List<Vector3>();
            foreach (var id in spriteIds)
            {
                if (spriteObjects.TryGetValue(id, out var go))
                {
                    targets.Add(go.transform);
                    positions.Add(go.transform.position);
                }
            }
            if (targets.Count < 2) yield break;

            // 确定性换位：每个 sprite 移到列表中下一个 sprite 的位置，最后一个移到第一个。
            var toPositions = new Vector3[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                toPositions[i] = positions[(i + 1) % targets.Count];
            }

            yield return StartCoroutine(TutorialPrimitives.TweenShuffle(targets.ToArray(), toPositions, duration, easing));
        }
    }
}
