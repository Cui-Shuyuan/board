// BoardGameTutorial
// 扫描件抠图工具：把"宝石/黄金"这类**圆形 token** 从扫描图里抠出来，写成带 alpha 的 PNG。
//
// 为什么要在文件层面做（而不是只靠运行时）：
//   运行时那套 `CardImageLoader.ApplyTokenMask` 依赖"白底亮度阈值 + 圆检测"的启发式，
//   在这批扫描件上实测**没生效**：背景亮度 0.85 落在"半透明带"里，
//   四角 alpha 仍是 1.00、整张图 100% 不透明 —— 用户看到的就是"方形白边"。
//   （与格式无关：扫描机出 PNG 也不会自带透明，背景是物理存在的。）
//
//   烘焙成 PNG 之后：① 结果可以被程序检查；② 运行时不依赖启发式；
//   ③ 原始扫描件一个字节都不动（输出是 `<原名>_cutout.png`）。
//
// 除了抠圆，还会**裁到圆的外接正方形** —— 扫描件往往不是正方形（564x532 这种），
// 宝石旁边有留白；不裁的话"实物 43mm ↔ world_size 0.43"对不上、宝石也不居中。
//
//   Unity.exe -batchmode -projectPath <proj> \
//     -executeMethod BoardGameTutorial.Editor.ScanCutout.Run \
//     -cutoutDir <扫描件目录> [-cutoutDiag 1] [-cutoutOnly "白宝石.jpg"] \
//     -logFile <log> -quit
//
// 算法（**直接优化目标**，不是猜阈值）：
//   走过的弯路（都留在这儿，免得下次再走）：
//     · "白底亮度阈值 + 圆检测"：背景 0.85 落在半透明带 → 什么都没切掉。
//     · "二值 mask + 圆度评分"：圆度分不清"真圆盘"和"更小的同心圆盘" ——
//       阈值紧一点只剩宝石内的亮斑，圆度仍是 1.000，半径却小 15%（白宝石踩过）。
//     · "质心定圆心 + 外接矩形定半径"：松 mask 带一圈阴影，质心被拉偏 7%。
//   本质问题：**边界不能用二值阈值去猜**。所以现在改成给每个像素一个"宝石程度"权重：
//
//   1. g(pixel) = clamp01( (颜色距离(pixel, 背景色) - 0.03) / (0.12 - 0.03) )
//      —— 0.03 以下算底色、0.12 以上算确定性本体，中间线性过渡（白宝石的外圈正落在中间）
//   2. 圆心 = g 的加权质心
//   3. 半径 = **"被切掉的 g 之和 ≤ 1%" 里最小的那个 r**（距离直方图上扫一遍，O(N)）
//      —— 即"一点都不切到宝石，同时尽量不把底色圈进来"
//   4. alpha = 圆内 1、圆外 0，边缘 1px 线性过渡（抗锯齿）
//   5. 自检：四角全透明 / 圆心不透明 / 圆外无不透明像素 / 不透明占比 ≈ π/4 /
//      **被切掉的宝石权重 ≤ 2%** / **圆内底色权重 ≤ 3%**（后两条才是真正该看的数）
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial.Editor
{
    public static class ScanCutout
    {
        /// <summary>默认要处理的扫描件（圆形 token）。</summary>
        private static readonly string[] TokenSuffixes = { "宝石.jpg", "黄金.jpg", "宝石.png", "黄金.png" };

        /// <summary>颜色距离 ≤ 它 = 底色（含扫描噪点）。</summary>
        private const float BgBand = 0.03f;

        /// <summary>颜色距离 ≥ 它 = 确定性本体。</summary>
        private const float GemBand = 0.12f;

        /// <summary>允许被切掉的宝石权重占总量的比例。</summary>
        private const float MaxCut = 0.01f;

        /// <summary>半径再收一点，避开扫描件边缘的暗环/白晕。</summary>
        private const float Shrink = 0.5f;

        private static string Arg(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string dir = Arg(args, "-cutoutDir", Path.Combine(repoRoot, "games/splendor/media/card"));
            bool diagOnly = Arg(args, "-cutoutDiag", "0") == "1";
            string only = Arg(args, "-cutoutOnly", null);

            if (!Directory.Exists(dir))
            {
                Debug.LogError($"[Cutout] 目录不存在: {dir}");
                Exit(2);
                return;
            }

            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith("_cutout.png")) continue;          // 别把自己的产物再抠一遍
                bool wanted = false;
                foreach (var suffix in TokenSuffixes)
                    if (name.EndsWith(suffix)) wanted = true;
                if (only != null && !only.Contains(name)) wanted = false;
                if (wanted) files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0)
            {
                Debug.LogError($"[Cutout] 没找到要处理的扫描件（目录 {dir}）");
                Exit(2);
                return;
            }

            int failed = 0;
            Debug.Log($"[Cutout] 处理 {files.Count} 个圆形 token 扫描件（{dir}）{(diagOnly ? "（只诊断）" : "")}");
            foreach (var path in files)
                if (!ProcessOne(path, diagOnly)) failed++;

            Debug.Log($"[Cutout] 完成：{files.Count - failed} 成功 / {failed} 失败");
            Exit(failed == 0 ? 0 : 1);
        }

        private static bool ProcessOne(string path, bool diagOnly)
        {
            string name = Path.GetFileName(path);
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(src, File.ReadAllBytes(path)))
            {
                Debug.LogError($"[Cutout] {name}: 读不出图片");
                UnityEngine.Object.DestroyImmediate(src);
                return false;
            }
            int w = src.width, h = src.height;
            var px = src.GetPixels();
            Color bg = MedianCorner(px, w, h);

            // 1) 宝石程度权重 + 加权质心（圆心）
            var weight = new float[w * h];
            double sw = 0, swx = 0, swy = 0;
            float maxDist = 0f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float d = Distance(px[i], bg);
                    if (d > maxDist) maxDist = d;
                    float g = Mathf.Clamp01((d - BgBand) / (GemBand - BgBand));
                    weight[i] = g;
                    sw += g; swx += g * x; swy += g * y;
                }
            if (sw < 1f)
            {
                Debug.LogError($"[Cutout] {name}: 找不到宝石（整张图都和背景一样？）—— 没有输出");
                UnityEngine.Object.DestroyImmediate(src);
                return false;
            }
            float cx = (float)(swx / sw), cy = (float)(swy / sw);

            // 2) 距离直方图 → 选半径："一点都不切到宝石"里最小的那个 r
            int maxR = Mathf.CeilToInt(Mathf.Sqrt((float)w * w + (float)h * h)) + 2;
            var gemAt = new double[maxR + 1];    // 该半径上的宝石权重
            var bgAt = new double[maxR + 1];     // 该半径上的底色权重
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float dx = x - cx, dy = y - cy;
                    int r = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(dx * dx + dy * dy)), 0, maxR);
                    gemAt[r] += weight[i];
                    bgAt[r] += 1f - weight[i];
                }
            // 从外往里累加"会被切掉的宝石权重"，找到允许切的上界
            double gemTotal = sw;
            double cutSoFar = 0;
            int chosen = -1;
            for (int r = maxR; r >= 2; r--)
            {
                cutSoFar += gemAt[r];
                if (cutSoFar > MaxCut * gemTotal) { chosen = r + 1; break; }
            }
            if (chosen < 0) chosen = 2;
            float radius = Mathf.Max(2f, chosen - Shrink);

            // 3) 裁到圆的外接正方形 + 烘焙 alpha（RGB 保持扫描原样）
            int side = Mathf.Max(8, Mathf.RoundToInt(2f * (radius + 1f)));
            float ox = cx - side / 2f, oy = cy - side / 2f;
            var outPx = new Color[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int sxp = Mathf.RoundToInt(x + ox), syp = Mathf.RoundToInt(y + oy);
                    var c = (sxp >= 0 && sxp < w && syp >= 0 && syp < h)
                        ? px[syp * w + sxp]
                        : new Color(bg.r, bg.g, bg.b, 1f);
                    float dx = (x + 0.5f) - side / 2f, dy = (y + 0.5f) - side / 2f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    c.a = Mathf.Clamp01(radius + 0.5f - d);
                    outPx[y * side + x] = c;
                }
            var outTex = new Texture2D(side, side, TextureFormat.RGBA32, false);
            outTex.SetPixels(outPx);
            outTex.Apply();

            // 4) 自检（含真正该看的两条：切掉的宝石权重、圆内剩多少底色）
            float cornerMax = Mathf.Max(
                Mathf.Max(outPx[0].a, outPx[side - 1].a),
                Mathf.Max(outPx[(side - 1) * side].a, outPx[side * side - 1].a));
            float centerAlpha = outPx[(side / 2) * side + side / 2].a;
            int opaque = 0, outsideOpaque = 0;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    if (outPx[y * side + x].a <= 0.5f) continue;
                    opaque++;
                    float dx = (x + 0.5f) - side / 2f, dy = (y + 0.5f) - side / 2f;
                    if (Mathf.Sqrt(dx * dx + dy * dy) > radius + 1f) outsideOpaque++;
                }
            float frac = (float)opaque / (side * side);

            double cut = 0, halo = 0;
            for (int r = chosen + 1; r <= maxR; r++) cut += gemAt[r];
            for (int r = 0; r <= chosen - 1; r++) halo += bgAt[r];

            Debug.Log($"[Cutout] {name} {w}x{h} → {side}x{side} 背景=({bg.r:0.00},{bg.g:0.00},{bg.b:0.00}) " +
                      $"最大色差={maxDist:0.00} 圆心=({cx:0},{cy:0}) r={radius:0.0} → " +
                      $"四角max α={cornerMax:0.00} 圆心α={centerAlpha:0.00} 圆外不透明={outsideOpaque} " +
                      $"占比={frac:0.000}（理想 π/4={Mathf.PI / 4f:0.000}）｜切掉的宝石={100.0 * cut / gemTotal:0.00}% " +
                      $"圆内底色={100.0 * halo / Mathf.Max(1f, (float)(halo + sw - cut)):0.00}%");

            bool ok = cornerMax <= 0.01f && centerAlpha >= 0.99f && outsideOpaque <= 4
                      && Mathf.Abs(frac - Mathf.PI / 4f) < 0.06f
                      && cut / gemTotal <= 0.02 && halo / Mathf.Max(1f, (float)(halo + sw - cut)) <= 0.03;
            if (!ok)
                Debug.LogError($"[Cutout] {name}: 自检没过（四角全透明 / 圆心不透明 / 圆外无不透明 / 占比≈π/4 /" +
                               $"切掉的宝石≤2% / 圆内底色≤3%）");

            if (!diagOnly)
            {
                string outPath = Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + "_cutout.png");
                File.WriteAllBytes(outPath, outTex.EncodeToPNG());
                Debug.Log($"[Cutout]   → 写出 {Path.GetFileName(outPath)}（{new FileInfo(outPath).Length / 1024} KB）");
            }

            UnityEngine.Object.DestroyImmediate(src);
            UnityEngine.Object.DestroyImmediate(outTex);
            return ok;
        }

        // ── 小工具（不依赖外部库）──────────────────────────────────────────

        private static float Distance(Color c, Color bg)
        {
            float dr = c.r - bg.r, dg = c.g - bg.g, db = c.b - bg.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>四角小块的中位色 = 背景色（比平均稳，扫描件角落偶有杂点）。</summary>
        private static Color MedianCorner(Color[] px, int w, int h)
        {
            int pad = Mathf.Clamp(Mathf.Min(w, h) / 12, 4, 16);
            var rs = new List<float>(); var gs = new List<float>(); var bs = new List<float>();
            for (int y = 0; y < pad; y++)
                for (int x = 0; x < pad; x++)
                    foreach (var (ox, oy) in new[] { (x, y), (w - 1 - x, y), (x, h - 1 - y), (w - 1 - x, h - 1 - y) })
                    {
                        var c = px[oy * w + ox];
                        rs.Add(c.r); gs.Add(c.g); bs.Add(c.b);
                    }
            rs.Sort(); gs.Sort(); bs.Sort();
            int mid = rs.Count / 2;
            return new Color(rs[mid], gs[mid], bs[mid], 1f);
        }

        private static void Exit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#endif
        }
    }
}
