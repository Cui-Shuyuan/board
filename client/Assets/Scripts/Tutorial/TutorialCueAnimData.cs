// BoardGameTutorial
// cue 动画数据的强类型模型。
//
// 核心模型（2026-09 与用户讨论后修正）：
//   动画 = 维护一组组件的状态；组件状态 = 它在哪个 zone、以什么姿态。
//   一切动画都是「组件从 source zone 移动到 destination zone」，位置由 zone 的
//   布局规则推导，不在 cue 数据里手填坐标。
//
// 因此数据分两层：
//   anim/_stage/{game}.table.json   —— 牌桌事实：有哪些 zone、每个 zone 在哪、
//                                      模板长什么样、开局各 zone 里放什么
//   anim/{track}/{cue_id}.json      —— 这一条 cue 对牌桌做了什么（移动 / 高亮 / 显隐）
//
// 运行时维护 zone 占用状态；设置阶段「从镜头外飞进供应堆」与玩家「从供应堆拿到
// 持有区」是同一个 move 原语，只是 source zone 不同。
using System;
using System.Collections.Generic;

namespace BoardGameTutorial
{
    // ── 牌桌（stage）────────────────────────────────────────────────────

    [Serializable]
    public class StageDoc
    {
        public int schema_version;
        public string game_id;
        public string kind;
        public string note;
        public StageBoard board;


        public List<StageZone> zones;
        public List<StageTemplate> templates;
        public List<StageAnchor> anchors;
        public List<StageInitial> initial;
    }

    [Serializable]
    public class StageBoard
    {
        public string background = "#1E2126";
        public float camera_pitch = 50f;

        /// <summary>取景留白倍率（1.0 = 恰好装下 extent）。</summary>
        public float ortho_scale = 1.18f;

        /// <summary>
        /// 取景范围（世界坐标）。这是相机取景的**唯一依据**，
        /// 不再从 zone 布局反推——layout.cols 写错一次就让整个取景失准。
        /// </summary>
        public StageExtent extent;

        /// <summary>取景使用的宽高比；&lt;=0 表示用当前屏幕。离线出帧必须显式指定。</summary>
        public float aspect;
    }

    [Serializable]
    public class StageExtent
    {
        public float min_x;
        public float max_x;
        public float min_z;
        public float max_z;
    }

    [Serializable]
    public class StageZone
    {
        public string id;
        public string label;

        /// <summary>offstage = 镜头外的入场通道；zone = 桌面上的容器。</summary>
        public string role = "zone";

        public StagePoint center = new StagePoint();

        /// <summary>该区域专属的底板/高亮颜色（缺省用 stage 的 glow_zone 模板色）。</summary>
        public string palette;

        /// <summary>offstage 用：偏离桌心的距离。</summary>
        public float margin = 3f;

        public StageLayout layout = new StageLayout();

        /// <summary>逻辑容量，用于校验；实际排布由 layout 决定。</summary>
        public int capacity;

        /// <summary>
        /// 该 zone 内**单件**的占用尺寸（世界单位）。显式声明，
        /// 让布局校验和相机取景都不必猜「这里面放的是卡牌还是棋子」。
        /// 留空则按 layout 的步长推算。
        /// </summary>
        public StageSize size;

        /// <summary>
        /// 数量表现：
        ///   count —— 少量，摊开摆放，一眼能数出枚数；
        ///   stack —— 叠放显示，**超过 max_visible 个就只显示 max_visible 个**，多出来的压在最后一层。
        /// 没有「一堆」这个独立概念，就是一条显示规则；zone 里的实例数仍按实际数量存在。
        /// </summary>
        public StageDisplay display;
    }

    [Serializable]
    public class StageDisplay
    {
        /// <summary>count | stack</summary>
        public string mode = "count";

        /// <summary>stack 专用：逐层的错开量（世界单位）。越小压得越紧、露出的边越少。</summary>
        public float dx = 0.03f;
        public float dz = 0.03f;

        /// <summary>
        /// stack 模式最多显示几个。超过就只显示这么多，其余的压在最后一层。
        /// 每层错开多少由 dx/dz 决定：越小越密。
        /// </summary>
        public int max_visible = 8;
    }

    [Serializable]
    public class StageSize
    {
        public float w = 0.14f;
        public float h = 0.14f;
    }

    [Serializable]
    public class StagePoint
    {
        public float x;
        public float z;
    }

    [Serializable]
    public class StageLayout
    {
        public string type = "pile";

        /// <summary>槽位横向/纵向间距。</summary>
        public float x_step = 0.09f;
        public float z_step = 0.09f;
        public int cols = 4;

        /// <summary>同一槽位最多压几件，超过则向外扩。</summary>
        public int overflow = 1;
    }

    [Serializable]
    public class StageTemplate
    {
        public string id;
        public string shape = "gem";
        public string palette;
        public string sprite;

        /// <summary>真实扫描图（相对 games/{game}，例如 media/card/一级发展卡_绿.jpg）。找不到则回退到 shape 的程序化图形。</summary>
        public string face_image;

        /// <summary>翻转用的另一面（相对 games/{game}）。有它才能「边移动边翻转」。</summary>
        public string back_image;
        public float world_size = 0.10f;

        /// <summary>非正方形件（区域底板）的显式宽高；留空则由 world_size + 贴图比例决定。</summary>
        public float width;
        public float height;

        public float alpha = 1f;
        public float rotation;
        public int sorting_order;
        public bool highlight;
    }

    /// <summary>牌桌上固定不动的背景件（区域底板等），有 id 可以被事件 target。</summary>
    [Serializable]
    public class StageAnchor
    {
        public string id;
        public string template;

        /// <summary>这块底板代表哪些 zone；这些 zone 的 highlight 都打到它身上。</summary>
        public List<string> zones;

        public float x;
        public float z;
        public float y;
    }

    [Serializable]
    public class StageInitial
    {
        public string template;
        public string palette;
        public string zone;
        public int count = 1;

        /// <summary>放在 offstage 时的入场方向：auto / top / bottom / left / right。</summary>
        public string from;
    }

    // ── 单条 cue 的差异 ────────────────────────────────────────────────

    [Serializable]
    public class CueAnimDoc
    {
        public int schema_version;
        public string game_id;
        public string track;
        public string cue;
        public string note;

        /// <summary>牌桌文件相对 tutorial/anim 的路径（不含扩展名）。</summary>
        public string stage = "_stage/splendor.table";

        /// <summary>本条 cue 播放前对状态做的准备（清空 / 预置），用于单独预览或表达初始局面。</summary>
        public CueAnimStart start;

        public List<CueAnimEvent> events;
    }

    /// <summary>
    /// 一条 cue 的入口状态。留空 = 沿用上一条 cue 的终态（顺序播放的默认情形）。
    /// 设置阶段那种「宝石还在镜头外」的局面，就可以用 clear 表达。
    /// </summary>
    [Serializable]
    public class CueAnimStart
    {
        public bool clear;

        /// <summary>预置内容；expand_to 表示「补到 N 件」。</summary>
        public List<CueAnimSeed> set;
    }

    [Serializable]
    public class CueAnimSeed
    {
        public string template;
        public string palette;
        public string zone;
        public int count = 1;

        /// <summary>补到 N 件（0 = 未指定）。JsonUtility 不支持可空类型，只能用哨兵值。</summary>
        public int expand_to;
        public string from;
    }

    [Serializable]
    public class CueAnimEvent
    {
        /// <summary>目标 zone（move 的目的地）。</summary>
        public string zone;

        /// <summary>目标 actor id / group / anchor id，用于非 move 动作和显式指定。</summary>
        public string target;

        /// <summary>move / rotate / flip / scale / fade / highlight / shuffle / wait</summary>
        public string action = "move";

        /// <summary>相对当前 cue 音频开头的秒数。</summary>
        public float at;

        public float dur;

        /// <summary>缓动名，见 Easing。</summary>
        public string easing;

        /// <summary>执行前先等（把同一 at 的动作错开）。</summary>
        public float lead;

        /// <summary>
        /// move：源 zone。留空表示「组件原位」或按 target 指定。
        /// 写成数组时表示「从这几个 zone 各取 take 件」（例如三种宝石各取一枚）。
        /// </summary>
        public List<string> from;

        /// <summary>move：从每个 from zone 搬几件（0 = 未指定，按 1 处理）。</summary>
        public int take;

        /// <summary>
        /// move：只搬这个模板的件（留空表示不限）。
        /// 同一个 zone 里混着不同组件时（例如盒子里的宝石和卡片），必须靠它区分，
        /// 否则按顺序取件会取到不该动的东西。
        /// </summary>
        public string template;

        public float stagger;

        /// <summary>move 时顺便翻面：到终点恰好转到另一面（用于「翻开四张牌」）。</summary>
        public bool flip;

        /// <summary>
        /// 目的 zone 内的落位序号。
        ///   -1 = 追加到末尾
        ///   ≥0 = 放到第 n 格
        ///   -2 = 只动画到该格位，不改占用（用于「牌从牌堆位置飞出、落到市场格」）
        /// </summary>
        public int order = -1;

        /// <summary>order=-2 时用它指定目标格位（避免和 order=-2 的语义冲突）。</summary>
        public int slot = -1;

        // ---- rotate / flip ----
        public float angle;

        // ---- scale ----
        /// <summary>by = 乘在基准缩放上；to = 直接设为该倍率。</summary>
        public string scale_mode;
        public float scale;

        // ---- fade ----
        /// <summary>目标透明度；负数 = 未指定（在 0/1 之间切换）。</summary>
        public float to_alpha = -1f;

        // ---- highlight ----
        /// <summary>高亮峰值透明度；负数 = 未指定（用默认）。</summary>
        public float peak_alpha = -1f;

        /// <summary>高亮放大倍率；0 或负数 = 未指定（用默认）。</summary>
        public float grow;
    }
}
