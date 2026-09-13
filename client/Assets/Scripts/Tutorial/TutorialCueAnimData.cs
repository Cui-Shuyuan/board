// BoardGameTutorial
// cue 动画数据的强类型模型。
//
// 数据：games/{game}/tutorial/anim/{track}/{cue_id}.json
// 与 full.runtime.json 分离：runtime 只描述音频/字幕/导航，这里只描述画面与动作。
//
// 设计约束：
// 1. 一条 cue 一个文件，文件里自带这张画面需要的全部 actor 和它们的初始状态。
//    播放端 ResetToStart() = 重建 actors，因此重播天然从头，不需要运行端状态回滚，
//    也为将来「编译期生成每个 cue 的起始画面」留好接口。
// 2. 事件只允许 8 个原语：move / flip / rotate / scale / fade / highlight / shuffle / wait。
//    参数是数据，不允许在 Unity 里为单条动画新写协程。
// 3. 时间都是相对当前 cue 音频开头的秒数，必须落在音频时长内。
using System;
using System.Collections.Generic;

namespace BoardGameTutorial
{
    [Serializable]
    public class CueAnimDoc
    {
        public int schema_version;
        public string game_id;
        public string track;
        public string cue;
        public string note;
        public CueAnimScene scene;
        public List<CueAnimEvent> events;
    }

    [Serializable]
    public class CueAnimScene
    {
        public string background = "#1E2126";
        public bool fit_camera = true;
        public float camera_pitch = 50f;
        public float ortho_scale = 1.25f;
        public List<CueAnimActorDef> actors;
    }

    [Serializable]
    public class CueAnimActorDef
    {
        /// <summary>唯一 id，事件用 target 引用它，也用于把 slot 分组（group）。</summary>
        public string id;

        /// <summary>语义分组，事件 target 可以直接指向分组批量操作。</summary>
        public string group;

        /// <summary>可见性分组：none / supply / holding。只为调试标注重心，不影响动画逻辑。</summary>
        public string zone;

        /// <summary>占位色板名，例如 gem_diamond / gem_gold。见 CueAnimPalette。</summary>
        public string palette;

        /// <summary>可选：真实贴图的 Resources 路径（不含扩展名）。留空则用程序化占位图形。</summary>
        public string sprite;

        /// <summary>占位图形：panel / gem / shadow / dot。</summary>
        public string shape = "gem";

        /// <summary>世界坐标（x 向右，z 向远处；y 由地面高度决定）。</summary>
        public float x;
        public float z;
        public float y;

        /// <summary>最长边的世界尺寸（单位同世界坐标）。</summary>
        public float world_size = 0.26f;

        /// <summary>基础透明度。</summary>
        public float alpha = 1f;

        /// <summary>绕本地 Z 轴的平面旋转（度）。</summary>
        public float rotation;

        /// <summary>基础缩放（叠加在 world_size 之上）。</summary>
        public float scale = 1f;

        public int sorting_order;

        /// <summary>在另一个 actor 上叠一个高亮原语（顺序在列表后方 = 更高层）。</summary>
        public bool highlight;
    }

    [Serializable]
    public class CueAnimEvent
    {
        /// <summary>目标 actor id 或 group 名。留空表示全体。</summary>
        public string target;

        /// <summary>相对当前 cue 音频开头的秒数。</summary>
        public float at;

        /// <summary>时长（秒）。</summary>
        public float dur;

        /// <summary>move / flip / rotate / scale / fade / highlight / shuffle / wait</summary>
        public string action;

        /// <summary>缓动名，见 Easing。留空用 easeOutCubic。</summary>
        public string easing;

        // ---- move ----
        [Serializable]
        public class MoveArgs
        {
            public float dx;
            public float dy;
            public float dz;
            public string to_slot;
            public float? to_x;
            public float? to_z;
        }

        public MoveArgs move;

        // ---- rotate / flip ----
        public float angle;

        // ---- scale ----
        /// <summary>by = 乘在基准缩放上；to = 直接设为该倍率。</summary>
        public string scale_mode;
        public float scale;

        // ---- fade ----
        public float? to_alpha;
        public bool hide_at_end;

        // ---- highlight ----
        public float? peak_alpha;
        public float? grow;

        /// <summary>执行前先等（用于把同一 at 的动作错开）。</summary>
        public float lead;
    }
}
