// BoardGameTutorial — 扫描件测量
//
// 用途（编辑器工具）：
//   1. 量出每张扫描件里「实体」的包围盒，以及圆形 token 的圆心半径；
//   2. 用卡背这个已知尺寸（63×88mm）当比例尺，反推宝石/黄金的实际直径；
//   3. 报告写到 client/CaptureOut/scan_report.txt，供决定 world_size 与遮罩参数。
//
// 关键点：JPEG 白底带灰噪点，不能用一个固定亮度阈值去猜「这是背景」——
// 先取四角的中位色作为背景基准，再按「与背景的色距」判定实体。
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BoardGameTutorial.Editor
{
    public static class ScanInspector
    {
        private const string CardDir = "games/splendor/media/card";
        private const string OutputName = "scan_report.txt";

        public static void Inspect()
        {
            string repoRoot = Path.Combine(Application.dataPath, "..", "..");
            string cardDir = Path.Combine(repoRoot, CardDir);
            string outDir = Path.Combine(Application.dataPath, "..", "CaptureOut");
            Directory.CreateDirectory(outDir);

            var files = Directory.GetFiles(cardDir, "*.jpg");
            System.Array.Sort(files);

            var sb = new StringBuilder();
            sb.AppendLine("# 扫描件测量");
            sb.AppendLine("# file\tpixels\tbgRGB\tbbox\tbw\tbh\tfill\t形状");

            var entries = new List<Entry>();
            foreach (var file in files)
            {
                var tex = LoadJpg(file);
                if (tex == null) continue;
                entries.Add(Measure(file, tex));
                Object.DestroyImmediate(tex);
            }

            foreach (var e in entries)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0}\t{1}x{2}\t({3},{4},{5})\t({6},{7})-({8},{9})\t{10}\t{11}\t{12:0.000}\t{13}",
                    e.Name, e.W, e.H, (int)(e.Bg.r * 255), (int)(e.Bg.g * 255), (int)(e.Bg.b * 255),
                    e.X0, e.Y0, e.X1, e.Y1, e.BoxW, e.BoxH, e.Fill, e.Round ? "圆" : "方"));
            }

            Entry calib = null;
            foreach (var e in entries)
            {
                if (!e.Name.Contains("背面")) continue;
                if (calib == null || e.BoxW > calib.BoxW) calib = e;
            }
            if (calib != null)
            {
                float mmPerPx = 63f / calib.BoxW;   // 发展卡实物宽 63mm
                sb.AppendLine();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "# 比例尺：{0} 卡宽 {1}px = 63mm → {2:0.0000} mm/px（卡高 {3}px 应为 {4:0.0}mm）",
                    calib.Name, calib.BoxW, mmPerPx, calib.BoxH, calib.BoxH * mmPerPx));

                sb.AppendLine();
                sb.AppendLine("# 圆形 token 实径（按卡牌比例尺换算）");
                foreach (var e in entries)
                {
                    if (!e.Round) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "# {0}\t直径 {1}px\t→ {2:0.1} mm\t圆心 ({3:0.0},{4:0.0}) 半径 {5:0.0}px",
                        e.Name, e.CircleD, e.CircleD * mmPerPx, e.Cx, e.Cy, e.CircleR));
                }

                sb.AppendLine();
                sb.AppendLine("# 方形板块（贵族）实宽");
                foreach (var e in entries)
                {
                    if (e.Round || e.Name.Contains("发展卡")) continue;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "# {0}\t宽 {1}px\t→ {2:0.1} mm",
                        e.Name, e.BoxW, e.BoxW * mmPerPx));
                }
            }

            File.WriteAllText(Path.Combine(outDir, OutputName), sb.ToString());
            Debug.Log($"[ScanInspector] 报告: {Path.Combine(outDir, OutputName)}");
            EditorApplication.Exit(0);
        }

        private class Entry
        {
            public string Name;
            public int W, H;
            public Color Bg;
            public int X0, Y0, X1, Y1;
            public int BoxW, BoxH;
            public float Fill;
            public bool Round;
            public float Cx, Cy, CircleR, CircleD;
        }

        private static Entry Measure(string path, Texture2D tex)
        {
            var px = tex.GetPixels();
            int w = tex.width, h = tex.height;

            // 背景基准：四角各取一小块的中位色
            var corners = new List<Color>();
            int pad = Mathf.Clamp(Mathf.Min(w, h) / 12, 4, 16);
            for (int y = 0; y < pad; y++)
                for (int x = 0; x < pad; x++)
                {
                    corners.Add(px[y * w + x]);
                    corners.Add(px[y * w + (w - 1 - x)]);
                    corners.Add(px[(h - 1 - y) * w + x]);
                    corners.Add(px[(h - 1 - y) * w + (w - 1 - x)]);
                }
            var bg = Median(corners);

            const float dist = 0.10f;
            int x0 = w, x1 = -1, y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (ColorDist(px[y * w + x], bg) <= dist) continue;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            if (x1 < 0) { x0 = 0; x1 = w - 1; y0 = 0; y1 = h - 1; }

            var e = new Entry
            {
                Name = Path.GetFileNameWithoutExtension(path),
                W = w, H = h, Bg = bg,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                BoxW = x1 - x0 + 1, BoxH = y1 - y0 + 1,
            };
            e.Fill = e.BoxW * e.BoxH / (float)(w * h);
            e.Round = e.Name.Contains("宝石") || e.Name.Contains("黄金");

            if (e.Round)
            {
                // 圆形：逐行扫描最宽的连续实体段，即直径所在行
                float best = 0f, bestCy = 0f, bestCx = 0f, bestLeft = 0f, bestRight = 0f;
                for (int y = Mathf.Max(0, y0 - 2); y <= Mathf.Min(h - 1, y1 + 2); y++)
                {
                    int left = -1, right = -1;
                    for (int x = 0; x < w; x++)
                    {
                        if (ColorDist(px[y * w + x], bg) <= dist) continue;
                        if (left < 0) left = x;
                        right = x;
                    }
                    if (left < 0) continue;
                    float span = right - left + 1;
                    if (span > best) { best = span; bestCy = y; bestLeft = left; bestRight = right; }
                }
                if (best > 0)
                {
                    e.CircleD = best;
                    e.CircleR = best * 0.5f;
                    e.Cy = bestCy;
                    e.Cx = (bestLeft + bestRight) * 0.5f;
                }
            }

            return e;
        }

        private static float ColorDist(Color a, Color b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static Color Median(List<Color> colors)
        {
            colors.Sort((a, b) => (a.r + a.g + a.b).CompareTo(b.r + b.g + b.b));
            return colors[colors.Count / 2];
        }

        private static Texture2D LoadJpg(string path)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { return null; }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }
    }
}
