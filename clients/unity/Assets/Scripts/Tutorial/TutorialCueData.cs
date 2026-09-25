// BoardGameTutorial
// full.runtime.json 的强类型数据模型。
//
// 只为「纯音频 cue 播放器」服务：每个 cue 自带 mp3、时长、字幕和导航分组。
// 动画不塞在这里；后续动画数据按 cue id 挂独立文件。
using System;
using System.Collections.Generic;

namespace BoardGameTutorial
{
    [Serializable]
    public class TutorialCueDoc
    {
        public int schema_version;
        public string game_id;
        public string track;
        public string title;
        public string voice;
        public string format;
        public int sample_rate;
        public string generator;
        public List<TutorialCue> cues;
    }

    [Serializable]
    public class TutorialCue
    {
        public string id;
        public string group;
        public List<string> group_path;
        public string text;
        public string audio;
        public float duration;
        public float start;
        public List<string> refs;
        public List<TutorialCueSubtitle> subtitles;
        public string animation;

        /// <summary>
        /// 本条 cue 的**入口状态**来自哪条 cue 的终态。
        ///
        ///   "" 或 null   → 继承「上一条 cue」的终态（顺序播放的默认）
        ///   "initial"    → 牌桌初始状态（只有 stage.initial 摆好的样子）
        ///   "&lt;cue id&gt;"   → 那条 cue 的终态
        ///
        /// 为什么需要它：像「可以抽一张」和「不可以抽两张」这种对照教学，
        /// 两条 cue 必须都从同一个状态出发（否则演示第二条时手里已经有牌了），
        /// 它们是**兄弟**而不是父子。有了这个字段，分支教学就是给两条 cue
        /// 写同一个 entry。
        /// </summary>
        public string entry;
    }

    [Serializable]
    public class TutorialCueSubtitle
    {
        public float t;
        public float end;
        public string text;
        public List<TutorialCueWord> words;
    }

    [Serializable]
    public class TutorialCueWord
    {
        public string word;
        public float start;
        public float end;
        public float confidence;
    }
}
