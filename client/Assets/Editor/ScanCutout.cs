// BoardGameTutorial
// 扫描件抠图工具：把"宝石/黄金"这类**圆形 token** 从扫描图里抠出来，写成带 alpha 的 PNG。
//
// 为什么要在文件层面做（而不是只靠运行时）：
//   运行时那套 `CardImageLoader.ApplyTokenMask` 依赖"白底亮度阈值 + 圆检测"的启发式，
//   在这批扫描件上实测**没生效**：背景亮度 0.85 落在"半透明带"里，
//   四角 alpha 仍是 1.00、整张图 100% 不透明 —— 用户看到的就是"方形白边"。
//   （与格式无关：扫描机出 PNG 也不会自带透明，背景是物理存在的。）
//
//   烘焙成 PNG 之后：① 结果可以被程序检查（四角必须透明、圆心必须不透明、
//   不透明面积 ≈ 圆面积 = π/4）；② 运行时不依赖启发式；③ 原始扫描件一个字节都不动。
//
// 除了抠圆，还会**裁到圆的外接正方形** —— 扫描件往往不是正方形（564x532 这种），
// 宝石旁边有留白；不裁的话"实物 43mm ↔ world_size 0.43"对不上、宝石也不居中。
//
//   Unity.exe -batchmode -projectPath <proj> \
//     -executeMethod BoardGameTutorial.Editor.ScanCutout.Run \
//     -cutoutDir <扫描件目录> [-cutoutDiag 1] [-cutoutOnly "白宝石.jpg"] \
//     -logFile <log> -quit
//
// 算法（**确定性**，不猜）：
//   1. 背景色 = 四角小块的中位色（扫描台的底色）
//   2. 前景 mask = 与背景色的**颜色距离** > 容差；填洞后取最大连通域（去噪点）
//   3. 容差**自适应**：试几个候选，选"最像个圆"的那个（面积 ≈ 外接矩形内切圆面积）
//   4. 圆心/半径：质心 + r = sqrt(area/π)，与外接矩形交叉验证（差太多就报出来）
//   5. 裁到 (cx,cy) 为心、边长 2r 的正方形；alpha = 圆内 1、圆外 0，边缘 1px 线性过渡
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

        /// <summary>候选容差（RGB 欧氏距离）。自适应挑最像圆的那个。</summary>
        private static readonly float[] Tolerances = { 0.04f, 0.06f, 0.08f, 0.10f, 0.13f, 0.16f, 0.20f, 0.25f };

        /// <summary>拟合半径再收一点点，避开扫描件边缘的暗环/白晕。</summary>
        private const float Shrink = 0.8f;

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
                if (name.EndsWith("_cutout.png")) continue;
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

            // 自适应容差：**在不失圆的前提下，取最大的那个盘**。
            //
            // 为什么不是"最像圆的那一个"：圆度分不清"真圆盘"和"更小的同心圆盘" ——
            // 阈值太紧时只剩宝石内的亮斑，圆度依然是 1.000，但半径小一大截，
            // 结果会把宝石外圈切掉（白宝石踩过：r=222.7 而真圆≈265）。
            // 曲线形状是：容差放松 → mask 变大且仍是圆盘；再放松 → 泄漏到背景/阴影，圆度掉下来。
            // 所以判据是"圆度合格（0.97~1.05）里，面积最大的那个"。
            float bestTol = -1f, bestRound = 0f;
            bool[] bestMask = null, coreMask = null;
            int bestArea = -1, coreArea = int.MaxValue;
            var table = new System.Text.StringBuilder();
            foreach (var tol in Tolerances)
            {
                var mask = LargestBlob(FillHoles(Foreground(px, w, h, bg, tol), w, h), w, h);
                int area = Count(mask);
                if (area < w * h / 20)
                {
                    table.Append($" {tol:0.00}→太小");
                    continue;
                }
                Bounds(mask, w, h, out float bx0, out float by0, out float bx1, out float by1);
                float bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
                float round = area / (Mathf.PI * (bw / 2f) * (bh / 2f));   // 圆 = 1.0
                table.Append($" {tol:0.00}→(r{Mathf.Sqrt(area / Mathf.PI):0},圆{round:0.000})");
                bool discLike = round >= 0.97f && round <= 1.05f;
                if (discLike && area > bestArea)
                {
                    bestArea = area; bestTol = tol; bestRound = round; bestMask = mask;
                }
                // 同时记下**最小**的圆盘：它是宝石"本体"（不含阴影/反光外圈），用来定圆心
                if (discLike && area < coreArea) { coreArea = area; coreMask = mask; }
            }
            Debug.Log($"[Cutout]   {name} 容差候选：{table}");
            if (bestMask == null)
            {
                Debug.LogError($"[Cutout] {name}: 找不到前景（宝石和背景几乎同色？）—— 没有输出");
                UnityEngine.Object.DestroyImmediate(src);
                return false;
            }

            // 圆心/半径：质心 + 面积反推半径，并与外接矩形交叉验证
            // 圆心取**本体**（最小圆盘）的质心：松 mask 常带一圈阴影/反光，质心会被拉偏
            //（白宝石那次偏了 18px = 7% 半径），拿它当圆心会切到宝石一侧。
            float cxs, cys; int coreN;
            Centroid(coreMask != null ? coreMask : bestMask, w, h, out cxs, out cys, out coreN);
            int n; float bxs, bys;
            Centroid(bestMask, w, h, out bxs, out bys, out n);
            float rArea = Mathf.Sqrt(n / Mathf.PI);
            Bounds(bestMask, w, h, out float mx0, out float my0, out float mx1, out float my1);
            float rBox = Mathf.Min(mx1 - mx0 + 1, my1 - my0 + 1) / 2f;
            float r = Mathf.Min(rArea, rBox) - Shrink;
            float mismatch = Mathf.Abs(rArea - rBox) / Mathf.Max(1f, rBox);
            float centerShift = Mathf.Sqrt((bxs - cxs) * (bxs - cxs) + (bys - cys) * (bys - cys));
            float cx = cxs, cy = cys;
            if (centerShift > 0.05f * r)
                Debug.LogWarning($"[Cutout] {name}: 本体质心与最大圆盘质心差 {centerShift:0.0}px" +
                                 $"（{100f * centerShift / Mathf.Max(1f, r):0}% 半径）—— 松 mask 里混进了阴影/反光？" +
                                 $"圆心按本体质心 ({cx:0},{cy:0}) 取");

            // 裁到圆的外接正方形（边长 2r + 1px 余量，保证边缘过渡不贴边）
            int side = Mathf.Max(8, Mathf.RoundToInt(2f * (r + 1f)));
            float ox = cx - side / 2f;      // 输出图里圆心的位置
            float oy = cy - side / 2f;
            var outPx = new Color[side * side];
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    int sx2 = Mathf.RoundToInt(x + ox), sy2 = Mathf.RoundToInt(y + oy);
                    var c = (sx2 >= 0 && sx2 < w && sy2 >= 0 && sy2 < h)
                        ? px[sy2 * w + sx2]
                        : new Color(bg.r, bg.g, bg.b, 1f);
                    float dx = (x + 0.5f) - side / 2f, dy = (y + 0.5f) - side / 2f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    c.a = Mathf.Clamp01(r + 0.5f - d);            // 1px 线性过渡 = 抗锯齿
                    outPx[y * side + x] = c;
                }

            var outTex = new Texture2D(side, side, TextureFormat.RGBA32, false);
            outTex.SetPixels(outPx);
            outTex.Apply();

            // 自检：四角全透明 / 圆心不透明 / 圆外没有不透明像素 / 不透明占比 ≈ π/4
            float cornerMax = Mathf.Max(
                Mathf.Max(outPx[0].a, outPx[side - 1].a),
                Mathf.Max(outPx[(side - 1) * side].a, outPx[side * side - 1].a));
            float centerAlpha = outPx[(side / 2) * side + side / 2].a;
            int opaque = 0, outside = 0;
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    if (outPx[y * side + x].a <= 0.5f) continue;
                    opaque++;
                    float dx = (x + 0.5f) - side / 2f, dy = (y + 0.5f) - side / 2f;
                    if (Mathf.Sqrt(dx * dx + dy * dy) > r + 1f) outside++;
                }
            float frac = (float)opaque / (side * side);

            Debug.Log($"[Cutout] {name} {w}x{h} → {side}x{side} 背景=({bg.r:0.00},{bg.g:0.00},{bg.b:0.00}) " +
                      $"容差={bestTol:0.00} 圆心=({cx:0},{cy:0})（最大盘质心 ({bxs:0},{bys:0})，差 {centerShift:0.0}px）r={r:0.0}" +
                      $"（面积法 {rArea:0.0} / 矩形 {rBox:0.0}，差 {mismatch:P0}）圆度={bestRound:0.000} → " +
                      $"四角max α={cornerMax:0.00} 圆心α={centerAlpha:0.00} 圆外不透明={outside} " +
                      $"不透明占比={frac:0.000}（理想 π/4={Mathf.PI / 4f:0.000}）");

            bool ok = cornerMax <= 0.01f && centerAlpha >= 0.99f && outside <= 4
                      && mismatch < 0.12f && Mathf.Abs(frac - Mathf.PI / 4f) < 0.06f;
            if (!ok)
                Debug.LogError($"[Cutout] {name}: 自检没过（四角要全透明、圆心要不透明、圆外不该有不透明像素、" +
                               $"面积法/矩形半径要接近、不透明占比要接近 π/4）");

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

        // ── 小工具（都不依赖外部库）────────────────────────────────────────

        /// <summary>与前景区分：颜色距离 > 容差（对彩色宝石和白色宝石都成立）。</summary>
        private static bool[] Foreground(Color[] px, int w, int h, Color bg, float tol)
        {
            var mask = new bool[w * h];
            for (int i = 0; i < mask.Length; i++)
            {
                var c = px[i];
                float dr = c.r - bg.r, dg = c.g - bg.g, db = c.b - bg.b;
                mask[i] = Mathf.Sqrt(dr * dr + dg * dg + db * db) > tol;
            }
            return mask;
        }

        /// <summary>填洞：从图像边界在"非前景"上泛洪，剩下的非前景就是内部孔洞 → 算作前景。</summary>
        private static bool[] FillHoles(bool[] fg, int w, int h)
        {
            var outside = new bool[w * h];
            var stack = new Stack<int>();
            for (int x = 0; x < w; x++) { Push(stack, outside, fg, x); Push(stack, outside, fg, (h - 1) * w + x); }
            for (int y = 0; y < h; y++) { Push(stack, outside, fg, y * w); Push(stack, outside, fg, y * w + w - 1); }
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                if (x > 0) Push(stack, outside, fg, i - 1);
                if (x < w - 1) Push(stack, outside, fg, i + 1);
                if (y > 0) Push(stack, outside, fg, i - w);
                if (y < h - 1) Push(stack, outside, fg, i + w);
            }
            var filled = new bool[w * h];
            for (int i = 0; i < filled.Length; i++) filled[i] = fg[i] || !outside[i];
            return filled;
        }

        private static void Push(Stack<int> stack, bool[] seen, bool[] fg, int i)
        {
            if (i < 0 || i >= fg.Length || fg[i] || seen[i]) return;
            seen[i] = true;
            stack.Push(i);
        }

        /// <summary>取最大连通域（4 邻接）。</summary>
        private static bool[] LargestBlob(bool[] mask, int w, int h)
        {
            var label = new int[w * h];
            var best = new bool[w * h];
            int bestSize = 0, tag = 0;
            var stack = new Stack<int>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || label[start] != 0) continue;
                tag++;
                var members = new List<int>();
                label[start] = tag;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    members.Add(i);
                    int x = i % w, y = i / w;
                    Try(label, stack, mask, i - 1, x > 0, tag);
                    Try(label, stack, mask, i + 1, x < w - 1, tag);
                    Try(label, stack, mask, i - w, y > 0, tag);
                    Try(label, stack, mask, i + w, y < h - 1, tag);
                }
                if (members.Count > bestSize)
                {
                    bestSize = members.Count;
                    best = new bool[w * h];
                    foreach (var m in members) best[m] = true;
                }
            }
            return best;
        }

        private static void Try(int[] label, Stack<int> stack, bool[] mask, int i, bool inside, int tag)
        {
            if (!inside || i < 0 || i >= mask.Length || !mask[i] || label[i] != 0) return;
            label[i] = tag;
            stack.Push(i);
        }

        private static void Centroid(bool[] mask, int w, int h, out float cx, out float cy, out int n)
        {
            double sx = 0, sy = 0;
            n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y * w + x]) { n++; sx += x; sy += y; }
            cx = n > 0 ? (float)(sx / n) : w / 2f;
            cy = n > 0 ? (float)(sy / n) : h / 2f;
        }

        private static int Count(bool[] mask)
        {
            int n = 0;
            foreach (var b in mask) if (b) n++;
            return n;
        }

        /// <summary>外接矩形（minX/minY/maxX/maxY；空 mask 时 max 为 -1）。</summary>
        private static void Bounds(bool[] mask, int w, int h,
                                   out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = w; minY = h; maxX = -1; maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y * w + x])
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
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
