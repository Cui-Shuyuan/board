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
