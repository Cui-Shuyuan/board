// BoardGameTutorial
// 扫描件抠图工具：把"宝石/黄金"这类**圆形 token** 从方形扫描图里抠出来，写成带 alpha 的 PNG。
//
// 为什么要在文件层面做（而不是只靠运行时）：运行时那套 `CardImageLoader.ApplyTokenMask`
// 依赖"能不能在图上找到那个圆"，找不到（背景不匀、有阴影）就只剩白底抠图，
// 四角的白边就留下来了 —— 而方形化正是用户看到的问题。
// 烘焙成 PNG 之后：① 结果可以被程序检查（四角必须透明、圆心必须不透明、不透明面积 ≈ πr²），
// ② 运行时不依赖启发式，③ 原始扫描件一个字节都不动（输出是 `<原名>_cutout.png`）。
//
//   Unity.exe -batchmode -projectPath <proj> \
//     -executeMethod BoardGameTutorial.Editor.ScanCutout.Run \
//     -cutoutDir <扫描件目录> [-cutoutDiag 1]  [-cutoutOnly "白宝石.jpg,黄金.jpg"] \
//     -logFile <log> -quit
//
// 复用 `CardImageLoader` 的 `DetectCircle` / `ApplyTokenMask` —— 与运行时同一套算法。
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BoardGameTutorial.Editor
{
    public static class ScanCutout
    {
        /// <summary>默认要处理的扫描件（圆形 token）：文件名以此结尾的都会被收进来。</summary>
        private static readonly string[] TokenSuffixes = { "宝石.jpg", "黄金.jpg" };

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
                EditorApplicationExit(2);
                return;
            }

            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith("_cutout.png")) continue;          // 别把自己的产物再抠一遍
                bool wanted = false;
                foreach (var suffix in TokenSuffixes)
                    if (name.EndsWith(suffix) || name.EndsWith(suffix.Replace(".jpg", ".png"))) wanted = true;
                if (only != null && !only.Contains(name)) wanted = false;
                if (wanted) files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);
            if (files.Count == 0)
            {
                Debug.LogError($"[Cutout] 没找到要处理的扫描件（目录 {dir}）");
                EditorApplicationExit(2);
                return;
            }

            int failed = 0;
            Debug.Log($"[Cutout] 处理 {files.Count} 个圆形 token 扫描件（目录 {dir}）{(diagOnly ? "（只诊断，不写文件）" : "")}");
            foreach (var path in files)
            {
                if (!ProcessOne(path, diagOnly)) failed++;
            }

            Debug.Log($"[Cutout] 完成：{files.Count - failed} 成功 / {failed} 失败");
            EditorApplicationExit(failed == 0 ? 0 : 1);
        }

        private static bool ProcessOne(string path, bool diagOnly)
        {
            string name = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes))
            {
                Debug.LogError($"[Cutout] {name}: 读不出图片");
                UnityEngine.Object.DestroyImmediate(tex);
                return false;
            }

            int w = tex.width, h = tex.height;
            // 处理前的诊断：圆找得到吗？背景是什么色？
            var before = tex.GetPixels();
            Vector3 circle = CardImageLoader.DetectCircle(before, w, h);
            Color bg = AverageCorner(before, w, h);

            // 跑**运行时那套**处理，把它的结果作为抠图结果（两边不会漂移）
            CardImageLoader.ApplyTokenMask(tex);
            var px = tex.GetPixels();

            // 自检：四角透明 / 圆心不透明 / 不透明面积 ≈ 圆面积 / 圆外没有不透明像素
            float cornerMax = Mathf.Max(
                Mathf.Max(px[0].a, px[w - 1].a),
                Mathf.Max(px[(h - 1) * w].a, px[(h - 1) * w + w - 1].a));
            float centerAlpha = px[(h / 2) * w + w / 2].a;
            int opaque = 0, outside = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = px[y * w + x];
                    if (c.a <= 0.5f) continue;
                    opaque++;
                    if (circle.z > 0f)
                    {
                        float dx = x - circle.x, dy = y - circle.y;
                        if (Mathf.Sqrt(dx * dx + dy * dy) > circle.z * 1.02f) outside++;
                    }
                }
            float opaqueFrac = (float)opaque / (w * h);
            float circleFrac = circle.z > 0f
                ? Mathf.PI * circle.z * circle.z / (w * h) : 0f;

            Debug.Log($"[Cutout] {name} {w}x{h} 背景=({bg.r:0.00},{bg.g:0.00},{bg.b:0.00}) " +
                      $"圆心=({circle.x:0},{circle.y:0}) r={circle.z:0} → " +
                      $"四角最大alpha={cornerMax:0.00} 圆心alpha={centerAlpha:0.00} " +
                      $"圆外不透明={outside} 不透明占比={opaqueFrac:0.000}（圆面积占比={circleFrac:0.000}）");

            bool ok = cornerMax <= 0.01f && centerAlpha >= 0.99f && circle.z > 0f;
            if (!ok)
                Debug.LogError($"[Cutout] {name}: 自检没过（四角必须全透明、圆心必须不透明、且必须找到圆）");

            if (!diagOnly)
            {
                string outPath = Path.Combine(Path.GetDirectoryName(path),
                    Path.GetFileNameWithoutExtension(path) + "_cutout.png");
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Debug.Log($"[Cutout]   → 写出 {Path.GetFileName(outPath)}（{new FileInfo(outPath).Length / 1024} KB）");
            }

            UnityEngine.Object.DestroyImmediate(tex);
            return ok;
        }

        /// <summary>四角一小块的平均色 = 背景色（与 DetectCircle 同一思路，只用于打印诊断）。</summary>
        private static Color AverageCorner(Color[] px, int w, int h)
        {
            int pad = Mathf.Clamp(Mathf.Min(w, h) / 12, 4, 16);
            float r = 0, g = 0, b = 0;
            int n = 0;
            for (int y = 0; y < pad; y++)
                for (int x = 0; x < pad; x++)
                {
                    foreach (var (ox, oy) in new[] { (x, y), (w - 1 - x, y), (x, h - 1 - y), (w - 1 - x, h - 1 - y) })
                    {
                        var c = px[oy * w + ox];
                        r += c.r; g += c.g; b += c.b; n++;
                    }
                }
            return new Color(r / n, g / n, b / n, 1f);
        }

        private static void EditorApplicationExit(int code)
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(code);
#endif
        }
    }
}
