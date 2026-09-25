// V2 batch sampler: no actor/camera/audio dependency.
//
// Usage (Unity batchmode):
//   -executeMethod BoardGameTutorial.Editor.TutorialV2Sampler.DumpStateV2
//   -v2Compiled <absolute path to *.compiled.json>
//   -v2Out <absolute sample path>
//   -v2Cues <comma separated cue ids>    (optional; default = all compiled cues)
using System;
using System.Collections.Generic;
using System.IO;
using BoardGameTutorial.Animation;
using UnityEditor;
using UnityEngine;

namespace BoardGameTutorial.Editor
{
    [Serializable]
    public class V2SampledItem
    {
        public string id;
        public string kind;
        public string concept;
        public string zone;
        public int order;
        public string face;
        public bool visible;
        public float x;
        public float z;
    }

    [Serializable]
    public class V2SampledCue
    {
        public string cue;
        public string picture;
        public List<V2SampledItem> items = new List<V2SampledItem>();
    }

    [Serializable]
    public class V2SampleDoc
    {
        public string schema = "tutorial-anim-sample/v2";
        public string track;
        public List<V2SampledCue> cues = new List<V2SampledCue>();
    }

    public static class TutorialV2Sampler
    {
        public static void DumpStateV2()
        {
            string compiledPath = Arg("-v2Compiled");
            string outPath = Arg("-v2Out");
            string cuesArg = Arg("-v2Cues");
            if (string.IsNullOrEmpty(compiledPath) || string.IsNullOrEmpty(outPath))
            {
                Debug.LogError("[TutorialV2Sampler] usage: -v2Compiled <path> -v2Out <path> [-v2Cues a,b,c]");
                EditorApplication.Exit(2);
                return;
            }
            if (!File.Exists(compiledPath))
            {
                Debug.LogError("[TutorialV2Sampler] compiled not found: " + compiledPath);
                EditorApplication.Exit(2);
                return;
            }

            var doc = JsonUtility.FromJson<CompiledTrackDef>(File.ReadAllText(compiledPath));
            if (doc == null || doc.cues == null)
            {
                Debug.LogError("[TutorialV2Sampler] parse failed: " + compiledPath);
                EditorApplication.Exit(1);
                return;
            }

            var runtime = new WorldRuntime();
            runtime.Load(doc);

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(cuesArg))
                foreach (var c in cuesArg.Split(','))
                    if (!string.IsNullOrEmpty(c.Trim())) wanted.Add(c.Trim());

            var sample = new V2SampleDoc { track = doc.track };
            foreach (var cue in doc.cues)
            {
                if (cue == null) continue;
                if (wanted.Count > 0 && !wanted.Contains(cue.id)) continue;
                var frame = runtime.Evaluate(cue.id, cue.duration);
                var sc = new V2SampledCue { cue = cue.id, picture = frame.Picture };
                foreach (var item in frame.Items)
                {
                    if (item == null || !item.Visible) continue;
                    sc.items.Add(new V2SampledItem
                    {
                        id = item.Id,
                        kind = (item.TemplateId ?? "") + "|" + (item.Palette ?? ""),
                        concept = item.TemplateId,
                        zone = item.ZoneId,
                        order = item.Order,
                        face = item.Face == FaceState.Up ? "up" : (item.Face == FaceState.Down ? "down" : "hidden"),
                        visible = item.Visible,
                        x = item.X,
                        z = item.Z,
                    });
                }
                sample.cues.Add(sc);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllText(outPath, JsonUtility.ToJson(sample, true));
            Debug.Log($"[TutorialV2Sampler] wrote {sample.cues.Count} cues -> {outPath}");
            EditorApplication.Exit(0);
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}
