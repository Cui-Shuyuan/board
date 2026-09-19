// BoardGameTutorial
// 「指示物」：指着某个件的**某个部位**画箭头 / 圈 / 禁止符号 / 叉。
//
// 为什么单独一个原语，而不是复用 highlight：
//   · highlight 是"整件/整区呼吸一下"，它没法只圈住牌角那个声望值 —— 而口播整节都在说
//     "左上角是声望、左下角是价格、右上角是折扣"；
//   · 指示物是**贴上去的图形**（箭头、红圈、禁止），它不改变件本身，也不改状态。
//
// 位置怎么来：`StageTemplate.part_anchors` 给出部位在**件平面内的相对偏移**（+x 右、+y 上，
// 世界单位）。世界位置 = 件此刻的位置 + 件的朝向 × 偏移 —— 所以牌被搬到市场、发展区、
// 或转了 roll，指示物都跟着走。这是"动画独有的 part 坐标"：本体说这张牌印着什么，
// 动画说它在牌面的哪儿。
//
// 时间语义：指示物是**时间的纯函数**——事件 `at` 之后出现，下一条 cue 开始时消失
// （`Until` 可以显式给结束时刻）。所以在 `Seek` 里来回拖、暂停、倒放都不会错。
using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial
{
    public partial class TutorialCueAnimPlayer
    {
        private class PointerMark
        {
            public GameObject Go;
            public SpriteRenderer Renderer;
            public float At;
            public float Until = -1f;   // <0 = 直到本条 cue 结束
        }

        private readonly List<PointerMark> pointers = new List<PointerMark>();
        private GameObject pointerRoot;

        /// <summary>指示物默认颜色（红圈）：口播里的"圈起来"用红色，与高亮的黄色区分开。</summary>
        private static readonly Color PointerColor = new Color(0.92f, 0.24f, 0.20f, 1f);

        private void TriggerPoint(CueAnimEvent ev)
        {
            // 纯表现：入口链重放（stateOnly）时不画（那时也没有场景对象，和 highlight 同理）
            if (stateOnly) return;

            var picked = Resolve(ev);
            if (picked.Count == 0)
            {
                Debug.LogWarning($"[TutorialCueAnim] point 没选中任何件（cue {CueId}）：" +
                                 $"what/zone/target={ev.what?.concept ?? ev.zone ?? ev.target}");
                return;
            }

            string indicator = string.IsNullOrEmpty(ev.indicator) ? "arrow" : ev.indicator;
            float lead = Mathf.Max(0f, ev.lead);
            float at = currentEventAt + lead;

            if (pointerRoot == null)
            {
                pointerRoot = new GameObject("Pointers");
                pointerRoot.transform.SetParent(transform, false);
            }

            int missing = 0;
            foreach (var actor in picked)
            {
                if (actor?.Go == null) continue;
                var tpl = actor.EffectiveTemplate ?? actor.Item?.Template;
                Vector3 offset = Vector3.zero;
                float r = 0f;
                if (!string.IsNullOrEmpty(ev.part))
                {
                    var anchor = FindPartAnchor(tpl, ev.part);
                    if (anchor == null) { missing++; continue; }
                    offset = new Vector3(anchor.dx, anchor.dy, 0f);
                    r = anchor.r;
                }
                // 件的朝向（= 与相机同朝向 × 件自己的 roll）带着偏移一起转
                Vector3 world = actor.Go.transform.position
                                + actor.Go.transform.rotation * offset;
                float worldR = r > 0f ? r : PieceRadius(tpl);

                var go = new GameObject($"point:{indicator}:{actor.Item?.Id}");
                go.transform.SetParent(pointerRoot.transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = PointerSprite(indicator);
                sr.color = PointerColor;
                sr.sortingOrder = 90;              // 盖在件与高亮之上
                go.transform.position = world;
                go.transform.rotation = actor.Go.transform.rotation;   // 与件同朝向（贴图本来就是屏幕对齐的）
                // 贴图按"直径 1 世界单位"生成，所以缩放 = 想要的直径
                float d = Mathf.Max(0.05f, worldR * 2f);
                go.transform.localScale = new Vector3(d, d, 1f);
                sr.enabled = false;                 // 由 SyncPointers 按时间开

                pointers.Add(new PointerMark { Go = go, Renderer = sr, At = at, Until = -1f });
            }
            if (missing > 0)
            {
                Debug.LogWarning($"[TutorialCueAnim] point 的部位 '{ev.part}' 在 {missing} 个件的模板上没有定义" +
                                 $"（模板 part_anchors 缺这个 id？cue {CueId}）");
            }
        }

        private static StagePart FindPartAnchor(StageTemplate tpl, string part)
        {
            if (tpl?.part_anchors == null) return null;
            foreach (var p in tpl.part_anchors)
                if (p != null && p.id == part) return p;
            return null;
        }

        /// <summary>没有声明部位时，按件的尺寸给一个能圈住整件的半径。</summary>
        private static float PieceRadius(StageTemplate tpl)
        {
            if (tpl == null) return 0.2f;
            if (tpl.width > 0f && tpl.height > 0f) return Mathf.Min(tpl.width, tpl.height) * 0.5f;
            return Mathf.Max(0.05f, tpl.world_size * 0.5f);
        }

        /// <summary>指示物是时间的纯函数：`at` 到了才显示，`Until` 过了或换 cue 就消失。</summary>
        private void SyncPointers()
        {
            for (int i = 0; i < pointers.Count; i++)
            {
                var p = pointers[i];
                if (p.Go == null || p.Renderer == null) continue;
                bool on = clock + 1e-4f >= p.At && (p.Until < 0f || clock <= p.Until + 1e-4f);
                p.Renderer.enabled = on;
            }
        }

        private void ClearPointers()
        {
            pointers.Clear();
            if (pointerRoot != null)
            {
                Object.DestroyImmediate(pointerRoot);
                pointerRoot = null;
            }
        }

        // ── 程序化图形（不需要美术素材）──────────────────────────────────────

        private static readonly Dictionary<string, Sprite> PointerSprites = new Dictionary<string, Sprite>();

        /// <summary>
        /// 生成指示物贴图：整张图按**直径 1 世界单位**画，所以用的时候缩放 = 想要的世界直径。
        /// 四种形状都用解析式画（圆环/斜杠/十字/箭头），再做 4× 超采样抗锯齿。
        /// </summary>
        private static Sprite PointerSprite(string indicator)
        {
            string key = string.IsNullOrEmpty(indicator) ? "arrow" : indicator;
            if (PointerSprites.TryGetValue(key, out var cached)) return cached;

            const int N = 256;
            const int S = 2;                     // 超采样倍数
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float acc = 0f;
                    for (int sy = 0; sy < S; sy++)
                        for (int sx = 0; sx < S; sx++)
                        {
                            // 归一化到 [-1,1]，+y 向上（贴图 v 向上，与件平面一致）
                            float u = ((x + (sx + 0.5f) / S) / N) * 2f - 1f;
                            float v = ((y + (sy + 0.5f) / S) / N) * 2f - 1f;
                            acc += ShapeCoverage(key, u, v);
                        }
                    float a = acc / (S * S);
                    px[y * N + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            var sprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N);
            PointerSprites[key] = sprite;
            return sprite;
        }

        /// <summary>形状覆盖判定（u,v ∈ [-1,1]，+y 上）。返回 0/1；多层形状取并集。</summary>
        private static float ShapeCoverage(string kind, float u, float v)
        {
            float r = Mathf.Sqrt(u * u + v * v);
            switch (kind)
            {
                case "circle":                        // 细圆环（圈住一个部位）
                    return r <= 0.98f && r >= 0.80f ? 1f : 0f;
                case "forbid":                        // 禁止：圆环 + 一条斜杠
                    if (r <= 0.98f && r >= 0.80f) return 1f;
                    // 斜杠：|u - v| 小、且长度受限
                    if (Mathf.Abs(u - v) <= 0.075f && r <= 0.95f) return 1f;
                    return 0f;
                case "cross":                         // 叉：两条对角粗线
                    if (Mathf.Abs(u - v) <= 0.10f && r <= 0.72f) return 1f;
                    if (Mathf.Abs(u + v) <= 0.10f && r <= 0.72f) return 1f;
                    return 0f;
                default:                              // arrow：从右下指向左上角的箭头，尖端落在 (-0.75,0.75) 附近
                {
                    // 箭杆：沿 (u+0.1) 与 (v-0.1) 的对角线方向，从中心到 (-0.35,0.35)
                    float a = (u + 0.1f) - (v - 0.1f);
                    if (Mathf.Abs(a) <= 0.055f && r <= 0.80f) return 1f;
                    // 箭头头部：以尖端为顶点的三角形（用两条半平面 + 一条底边切出来）
                    Vector2 tip = new Vector2(-0.72f, 0.72f);
                    Vector2 p = new Vector2(u, v);
                    Vector2 dir = new Vector2(1f, -1f).normalized;      // 由尖端指向箭杆
                    Vector2 perp = new Vector2(1f, 1f).normalized;
                    Vector2 d = p - tip;
                    float along = Vector2.Dot(d, dir);
                    float side = Mathf.Abs(Vector2.Dot(d, perp));
                    if (along >= 0f && along <= 0.34f && side <= along * 0.85f) return 1f;
                    return 0f;
                }
            }
        }
    }
}
