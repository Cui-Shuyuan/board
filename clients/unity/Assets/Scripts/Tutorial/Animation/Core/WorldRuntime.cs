// Stateless compiled-track lookup and per-cue evaluation facade.
using System;
using System.Collections.Generic;

namespace BoardGameTutorial.Animation
{
    /// Stateless per-cue runtime: every cue carries compiled start/end snapshots,
    /// so state resolution happens at compile time and jumping is a pure lookup.
    /// </summary>
    public sealed class WorldRuntime
    {
        private CompiledTrackDef track;
        private readonly Dictionary<string, CompiledCueDef> cues = new Dictionary<string, CompiledCueDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, CompiledStageDef> stages = new Dictionary<string, CompiledStageDef>(StringComparer.Ordinal);
        private readonly Dictionary<string, TreeDef> trees = new Dictionary<string, TreeDef>(StringComparer.Ordinal);

        public CompiledTrackDef Track => track;

        public void Load(CompiledTrackDef compiled)
        {
            track = compiled;
            cues.Clear(); stages.Clear(); trees.Clear();
            if (compiled?.cues != null)
                foreach (var c in compiled.cues)
                    if (c != null && !string.IsNullOrEmpty(c.id)) cues[c.id] = c;
            if (compiled?.stages != null)
                foreach (var s in compiled.stages)
                    if (s != null && !string.IsNullOrEmpty(s.stage)) stages[s.stage] = s;
            if (compiled?.trees != null)
                foreach (var t in compiled.trees)
                    if (t != null && !string.IsNullOrEmpty(t.id)) trees[t.id] = t;
        }

        public bool TryCue(string cueId, out CompiledCueDef cue) => cues.TryGetValue(cueId ?? "", out cue);

        public bool TryTree(string treeId, out TreeDef tree) => trees.TryGetValue(treeId ?? "", out tree);

        public CompiledStageDef StageForCue(CompiledCueDef cue)
        {
            if (cue == null) return null;
            if (!string.IsNullOrEmpty(cue.stage))
                return stages.TryGetValue(cue.stage, out var cueStage) ? cueStage : null;
            if (!trees.TryGetValue(cue.tree ?? "", out var tree)) return null;
            return stages.TryGetValue(tree.stage ?? "", out var stage) ? stage : null;
        }

        public FrameState Evaluate(string cueId, float t)
        {
            if (!TryCue(cueId, out var cue)) return new FrameState();
            var stage = StageForCue(cue);
            return TimelineEvaluator.Evaluate(cue, stage, t);
        }

        public float DurationOf(string cueId)
        {
            return TryCue(cueId, out var cue) ? cue.duration : 0f;
        }
    }
}
