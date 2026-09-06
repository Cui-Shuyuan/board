// BoardGameTutorial
// tutorial.json 的强类型数据模型。
//
// 设计原则：
// - 与 tutorial/schema/tutorial.schema.json 一一对应；
// - 只使用 UnityEngine.JsonUtility 支持的结构（可序列化类 + 数组 + 基本类型），
//   这样不依赖 Newtonsoft 也能解析；
// - 缺省值（easing/pivot/origin/pixels_per_unit 等）在运行时由 TutorialRuntime
//   统一补齐，JSON 里允许省略。
//
// 注意：JsonUtility 对缺失字段不会套用字段初始化值，因此这里的字段初始化只作
// 文档用途，真正兜底在 TutorialRuntime.NormalizeDefaults() 里做。
using System;

namespace BoardGameTutorial
{
    [Serializable]
    public class TutorialDoc
    {
        public TutorialMeta meta;
        public TutorialBoard board;
        public TutorialSlot[] slots;
        public TutorialSprite[] sprites;
        public TutorialChapter[] chapters;
    }

    [Serializable]
    public class TutorialMeta
    {
        public string game_id;
        public string version;
        public LocalizedText title;
        public string locale;
        public string default_easing;
        public string default_highlight_color;
    }

    [Serializable]
    public class LocalizedText
    {
        public string zh;
        public string en;
    }

    [Serializable]
    public class TutorialBoard
    {
        public string image;
        public float width_mm;
        public float height_mm;
        public string origin;           // "top_left" | "center"
        public float pixels_per_unit;   // 缺省 100
    }

    [Serializable]
    public class TutorialSlot
    {
        public string id;
        public float x;                 // 版图归一化坐标，origin 决定零点
        public float y;
        public float z;                 // 高度偏移（mm），缺省 0
        public LocalizedText label;
    }

    [Serializable]
    public class TutorialSprite
    {
        public string id;
        public string file;             // 相对 games/{game}/media/ 的 PNG 路径
        public float width_mm;
        public float height_mm;
        public string pivot;            // "center" | "top_left" | "bottom_center"
        public float z_offset;          // 叠放高度偏移（mm），缺省 0
        public bool hidden;             // 初始隐藏（alpha=0），配合 fade 原语做登场
        public string spawn_slot;       // 初始摆放 slot；缺省用第一个 move 的 from_slot
    }

    [Serializable]
    public class TutorialChapter
    {
        public string id;
        public LocalizedText title;
        public string audio;            // 相对 games/{game}/media/ 的音频路径
        public TutorialSubtitle[] subtitles;
        public string interrupt_context; // 打断问答时注入的上下文
        public TutorialEvent[] timeline;
    }

    [Serializable]
    public class TutorialSubtitle
    {
        public float t;
        public string text;
    }

    [Serializable]
    public class TutorialEvent
    {
        public string id;
        public float t;
        public float duration;
        public string action;           // move|flip|rotate|scale|fade|highlight|shuffle|wait

        // 单 sprite 目标
        public string sprite;

        // move 的端点：slot 引用优先；没有 slot 时才用 position
        public string from_slot;
        public string to_slot;
        public TutorialPosition from_position;
        public TutorialPosition to_position;

        // highlight 的 slot 目标
        public string slot;

        // shuffle 的 sprite 列表
        public string[] sprites;

        // 参数
        public float rotation;          // rotate：目标角度（度）
        public float scale;             // scale：目标倍率
        public float opacity;           // fade：目标透明度 0..1
        public string color;            // highlight：颜色 #RRGGBB
        public string easing;           // 缓动函数名
        public bool loop;               // highlight：是否循环脉冲
        public string comment;          // 备注，播放器忽略
    }

    [Serializable]
    public class TutorialPosition
    {
        public float x;
        public float y;
        public float z;
    }
}
