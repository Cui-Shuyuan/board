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
//   anim/{track}.json               —— 一个动画一个文件：每条 cue 一段（start/events 给引擎，story/enter/exit 给人和对账）
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

        /// <summary>
        /// **可以后来才长出来的 zone 的定义**（世界会长大）。
        ///
        /// 为什么定义放在 stage：脚本里**不写坐标**（坐标只属于 stage 这一层）。
        /// 脚本只说"现在把 `xxx` 这个区域开出来"（`{"action":"zone","op":"add","zone":"xxx"}`），
        /// 位置/布局/容量都从这里取。同一个定义要开多个时给 `index`，
        /// 第 N 个的 id 是 `xxx#N`、位置按 `repeat_x/repeat_z` 平移 ——
        /// 于是"排第几"仍然由 stage 说，脚本只报数。
        /// </summary>
        public List<StageZone> zone_defs;

        public List<StageTemplate> templates;
        public List<StageAnchor> anchors;

        /// <summary>
        /// 容器：把**任意一组**组件当成一个可操作对象。
        ///
        /// 与 zone 的区别：zone 是「恰好同属一个区域」，容器是「我点名要这一组」。
        /// 例如「某张牌 + 压在它上面的宝石」不属于同一个 zone，但可以是一个容器。
        /// 容器只是选择器的一种，不建父子节点 —— 组内每件仍是独立组件，
        /// 所以既能整组操作，也能单独操作其中一件。
        /// </summary>
        public List<StageContainer> containers;

        public List<StageInitial> initial;
    }

    [Serializable]
    public class StageBoard
    {
        public string background = "#1E2126";
        /// <summary>取景俯角（度）。90 = 正俯视（组件按原始尺寸显示）；小于 90 会带斜视纵深，
        /// 但组件面片会跟着相机转（见 TutorialCueAnimPlayer.SpriteRotation），所以不会被压扁。</summary>
        public float camera_pitch = 90f;

        /// <summary>
        /// 牌桌的**根画面**：默认展示的整幅图（例如背景介绍时显示游戏盒封面）。
        /// 这是「树根」的状态，事件 showbox 可以改变它。
        /// 放在 stage 而不是某条 cue 里，是为了让入口状态**从根开始解** ——
        /// 这样跳转与顺序播放得到同一画面（否则两条路会不一致）。
        /// </summary>
        public string default_picture;

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

        /// <summary>`zone_defs` 用：同一个定义实例化多个时的平移量（第 index 个乘它）。</summary>
        public float repeat_x;
        public float repeat_z;

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

        /// <summary>
        /// 本体 &lt;zone&gt;.contains：这个区域允许存放哪些概念（空数组 = 不限）。
        /// 本体的原话就是给程序用的——"程序校验 &lt;transfer&gt; 时以此过滤"。
        /// 纯视觉区（镜头外通道、展示位）写空数组。
        /// </summary>
        public List<string> contains;

        /// <summary>
        /// 这个 zone 是"**哪个概念的哪一份**"（stage 的绑定约定，不是本体字段 ——
        /// 本体里 zone 靠 id 区分；我们用 concept + parts 让它**可复用**）。
        ///
        /// 例：五个宝石供应堆的 concept 都是 `gem_supply`，各自 parts 是
        /// `color=<diamond>` / `<sapphire>` / …。于是脚本可以写
        /// `source: ["<gem_supply|color=<diamond>>"]` —— **不写本作专用的 zone id**，
        /// 换游戏/挪位置都不用改动画数据（位置本来就在 stage 里）。
        /// </summary>
        public List<StageNamedRef> parts;

        /// <summary>这个区域实例化的是哪个本体概念（null = 纯视觉）。</summary>
        public string concept;
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

    /// <summary>
    /// 一个「名字 → 引用」对。概念绑定里要表达 map（按色板分身份、卡面印记），
    /// 而 **JsonUtility 不支持字典** —— 写成 JSON 对象会被静默丢弃。
    /// 所以这些地方一律写成这种小对象的列表。
    /// </summary>
    [Serializable]
    public class StageNamedRef
    {
        public string key;      // 印记名（如 bonus）
        public string value;    // 指向的概念（如 <diamond>）
    }

    /// <summary>
    /// 件上的一个**可见部位**在件平面里的相对位置（动画层概念，见 StageTemplate.part_anchors）。
    /// 偏移是**世界单位**、相对件中心：+x 右、+y 上；件的朝向会带着它一起转。
    /// `r` 是圈选半径（circle / forbid 用）：圈要把那个部位刚好圈住。
    /// </summary>
    [Serializable]
    public class StagePart
    {
        public string id;        // "prestige" / "cost" / "bonus" / "condition" …
        public string label;     // 人读的说明："左上角：声望值"
        public float dx;
        public float dy;
        public float r = 0.06f;
    }

    /// <summary>
    /// `concept_by_palette` 的一项：某个色板对应哪个概念、带哪些属性。
    ///
    /// 为什么单独一个类型：以前它跟 `StageNamedRef` 共用一个类，于是那个类里
    /// 有个 `List&lt;StageNamedRef&gt; parts` —— **自己装自己**。
    /// Unity 的 JsonUtility 对递归类型会报
    /// `Serialization depth limit 10 exceeded ... object composition cycle`，
    /// 而且**超过 10 层就静默丢掉**：数据再深一点就会丢字段还查不出来（2026-09 采样时发现）。
    /// 拆成两个非递归类型，警告没了，超深丢字段的隐患也没了。
    /// </summary>
    [Serializable]
    public class StagePaletteBinding
    {
        public string palette;              // 色板名
        public string concept;              // 该色板对应的概念
        public List<StageNamedRef> parts;   // 这个色板对应的属性（留空则用模板的）
    }

    [Serializable]
    public class StageTemplate
    {
        public string id;
        public string shape = "gem";
        public string palette;
        public string sprite;

        /// <summary>
        /// 这个模板实例化的是**哪个本体概念**（ontology/concepts.json 或
        /// games/{game}/concepts.json 里的 id）。null = 纯视觉件（例如高亮底板）。
        ///
        /// 为什么放在模板上：动画与本体描述的是同一个世界，这个字段就是那根线。
        /// 校验器（scripts/validate_cue_anim.py）用它检查"能不能移进那个区域"、
        /// "这东西有没有正反面"，采样（DumpState）用它把状态导成本体的说法。
        /// </summary>
        public string concept;

        /// <summary>同一模板按色板分身份时用（宝石六面共用一个模板，但金黄是 &lt;gold&gt; 不是 &lt;gem&gt;）。</summary>
        public List<StagePaletteBinding> concept_by_palette;

        /// <summary>
        /// 本体 &lt;piece&gt;.parts：这块物理件上印着的**逻辑组件**（卡面的折扣色、
        /// 声望点数…）。用「拿刀裁开即独立 piece」来理解。
        /// </summary>
        public List<StageNamedRef> parts;

        /// <summary>
        /// **动画独有的「部位」概念**：这块件的某个可见部位，在这块件的**相对坐标**里在哪。
        ///
        /// 与上面的 `parts` 的区别：`parts` 是**本体**的说法（"这张牌印着白折扣"），只有语言、
        /// 没有位置；`part_anchors` 是**动画**的说法 ——「左上角那个声望值」对应这张牌上的哪个点、
        /// 圈多大。同为 parts，一层说"有什么"、一层说"在哪儿"，所以这里只写**相对件中心**的偏移
        /// （世界单位，件平面内：+x 右、+y 上），不写任何桌面坐标 —— 桌面坐标仍然只属于 zone。
        ///
        /// 有了它，"指着牌角讲"才有可能：`{"action":"point","part":"prestige","indicator":"arrow"}`
        /// 会把箭头画在某一件（或某一片）牌的左上角；这件被搬到哪、转多少度，指示物都跟着走
        /// （世界位置 = 件的位置 + 件的朝向 × 相对偏移）。
        /// </summary>
        public List<StagePart> part_anchors;

        /// <summary>真实扫描图（相对 games/{game}，例如 media/card/一级发展卡_绿.jpg）。找不到则回退到 shape 的程序化图形。</summary>
        public string face_image;

        /// <summary>翻转用的另一面（相对 games/{game}）。有它才能「边移动边翻转」。</summary>
        public string back_image;

        /// <summary>
        /// 入场起点落在这个 zone 的位置（而不是 offstage 中心）。
        /// 用于「市场牌从对应牌堆的位置飞出来」这类需求：牌真正归属仍在盒子里，
        /// 但出场位置对准牌堆。
        /// </summary>
        public string from_zone;
        public float world_size = 0.10f;

        /// <summary>非正方形件（区域底板）的显式宽高；留空则由 world_size + 贴图比例决定。</summary>
        public float width;
        public float height;

        public float alpha = 1f;
        public float rotation;
        public int sorting_order;
        public bool highlight;

        /// <summary>
        /// 介绍用的**替身**（样本卡、样本宝石）：外形与真件相同，但不是真件 ——
        /// 独立模板，所以不混进"每色 7 枚"这类账；介绍完就销毁。
        ///
        /// 为什么要标记：样本与真件绑**同一套概念**（一枚样本确实"是"那种宝石），
        /// 于是"按概念点名"会同时命中两者。凡是"要搬/要补一件真件"的判定
        /// （transfer 的 what、start.set 预置）都要用 `ConceptCandidatesReal` 把样本排除，
        /// 否则"盒里的黄金"会被判成"说不清是哪一件"、预置会被整条跳过。
        /// </summary>
        public bool sample;
    }

    /// <summary>
    /// 容器：任意一组组件的命名集合，可被事件当选择器使用。
    /// 与 zone 的区别是「点名的一组」而非「同属一个区域」。
    /// </summary>
    [Serializable]
    public class StageContainer
    {
        /// <summary>容器名，事件里用 container 引用。</summary>
        public string id;

        /// <summary>组内组件 id（可以任意组合，不要求同 zone）。</summary>
        public List<string> items;

        /// <summary>可选：组锚点。留空则用组内重心。</summary>
        public float x, z;
        public bool has_center;
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

    /// <summary>
    /// 一条 track 的动画脚本 —— **一个动画一个文件**（`anim/{track}.json`）。
    ///
    /// `cues` 按**轨道顺序**排；每条 cue 一段，段里同时放着两样东西：
    ///   - 给人 & 对账工具：`entry_from` / `story` / `note` / `enter` / `exit` / `timing`
    ///   - 给引擎：`start` / `events`
    ///
    /// **引擎只读后者**：本类与 <see cref="CueAnimDoc"/> 只声明引擎要用的字段，
    /// 契约那些字段 JsonUtility 会原样忽略 —— 这不是丢数据，是两条链路共用一份文件
    /// （所以"契约里写了、动画还没写"的 cue 允许 `events` 为空，引擎视为"本条无动画"）。
    /// 反过来说：`events` 写错名字这种错，引擎不会报，得靠 validate_cue_anim.py 的
    /// "events 为空"检查。两边各守一段。
    /// </summary>
    [Serializable]
    public class TrackAnimDoc
    {
        public int schema_version;
        public string game_id;
        public string track;
        public string stage;      // 这条 track 用哪张牌桌（相对 tutorial/anim）
        public string note;
        public List<CueAnimDoc> cues;
    }

    [Serializable]
    public class CueAnimDoc
    {
        public int schema_version;
        public string game_id;
        public string track;
        public string cue;
        public string note;

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
        /// <summary>
        /// 用本体语言说清"预置哪一种组件"（推荐）。与 transfer 的 what 同一套解析。
        /// 预置是**凭空造**，所以候选必须唯一 —— 说不清就直接报错，不猜。
        /// </summary>
        public ConceptRef what;

        public string template;
        public string palette;
        public string zone;
        public int count = 1;

        /// <summary>补到 N 件（0 = 未指定）。JsonUtility 不支持可空类型，只能用哨兵值。</summary>
        public int expand_to;
        public string from;
    }

    /// <summary>
    /// 用**本体语言**引用一个组件：它是哪个概念、带哪些属性。
    ///
    /// ```json
    /// "what": { "concept": "development_card_level_1",
    ///           "parts": [ { "key": "bonus", "value": "<emerald>" } ] }
    /// ```
    ///
    /// 为什么不写成本体文档里的 `{"<concept>": {属性}}`：**JsonUtility 不支持动态键**
    /// （只按固定字段名反序列化），那种写法引擎一个字都读不到 —— 和 `concept_by_palette`
    /// 一样，只能摊平成固定形状。字段名 `what` 取自本体自己的散文：
    /// `&lt;zone&gt;.contains` 写的是"若 **what** 的类型不在 contains 中，&lt;transfer&gt; 非法"。
    ///
    /// 引擎把它解析成具体的 (模板, 色板) —— **解析不出来或者不唯一都要报错**，
    /// 不允许"猜一个最像的"，那正是静默失败的老路。
    /// </summary>
    [Serializable]
    public class ConceptRef
    {
        public string concept;
        public List<StageNamedRef> parts;   // 用 key/value 两个字段
    }

    [Serializable]
    public class CueAnimEvent
    {
        /// <summary>目标 zone（move 的目的地）。</summary>
        public string zone;

        /// <summary>目标 actor id / group / anchor id，用于非 move 动作和显式指定。</summary>
        public string target;

        /// <summary>transfer / rotate / flip / scale / fade / highlight / shuffle / wait /
        /// showbox / create / destroy / stack（名字尽量与本体对齐：transfer = &lt;transfer&gt;）</summary>
        public string action = "transfer";

        /// <summary>相对当前 cue 音频开头的秒数。</summary>
        public float at;

        public float dur;

        /// <summary>缓动名，见 Easing。</summary>
        public string easing;

        /// <summary>执行前先等（把同一 at 的动作错开）。</summary>
        public float lead;

        /// <summary>create：一次创建几个（0 = 1）。</summary>
        public int count;


        /// <summary>create：色板名（决定颜色/贴图，例如 card_level_1）。</summary>
        public string palette;

        /// <summary>
        /// stack：这一摞里"真实存在的牌"，按逗号分隔的模板 id，**第一个 = 最先被发走的**
        /// （= 牌堆顶）。其余位置用 <see cref="pad_template"/> 补满到 <see cref="capacity"/>。
        ///
        /// 之所以要这个字段，是因为用 create 搭一摞牌有两个坑：
        ///   ① order 由"谁先建"决定（NextFreeSlot），所以真牌必须**后建**才在顶面 ——
        ///      规则隐晦，且发牌方向与 create 顺序耦合；
        ///   ② 一摞牌要写 1 + N 个 create 事件，三摞就是十几个。
        /// stack 把这个意图一步说清：给牌面顺序和垫牌模板，其余交给引擎。
        /// </summary>
        public string real_templates;

        /// <summary>stack：垫牌模板（凑数用的空白牌，显示卡背）。</summary>
        public string pad_template;

        /// <summary>stack：这一摞的总张数（含垫牌）。0 = 只用 real_templates 的张数。</summary>
        public int capacity;

        // create 的朝向改用上面的 `to`（"face_up"/"face_down"）：一个字段说清终态，
        // 不再有"两个布尔互斥"的空子可以钻（以前 face_up 和 face_down 同时写会静默取 face_up）。


        /// <summary>
        /// create：不指定色板（用模板自己的）。
        /// 用于发一整摞牌：牌堆里 40 张牌在发出去之前**不知道是哪张牌**，
        /// 所以不按颜色区分色板，整摞同色板即可。
        /// </summary>
        public bool plain;

        /// <summary>
        /// 取景目标：写 zone id 则把镜头对准该 zone（特写），写 "board" 或留空则回到整桌取景。
        /// 用于「这是发展卡牌」这类特写时刻 —— 否则卡在整桌取景里只占很小一块。
        /// </summary>
        public string camera;

        /// <summary>取景特写时的留白倍率（越大视野越宽、物体越小）。默认 2.2。</summary>
        public float camera_padding;

        /// <summary>
        /// showbox：要显示/隐藏的图片（相对 games/{game}，例如 media/box.png）。
        /// 配 action="showbox"，on=1 显示、on=0 隐藏。
        /// </summary>
        public string picture;

        /// <summary>showbox：1=显示、0=隐藏。</summary>
        public float on = 1f;

        /// <summary>
        /// 选择器：容器名。与 target/zone 同级，优先级 target &gt; container &gt; zone。
        /// 容器可以装任意一组组件，用于「整组一起动」。
        /// </summary>
        public string container;

        /// <summary>
        /// shuffle：强度倍率（0 = 用默认 1.0）。
        /// 大牌堆/小棋子可以用不同的量；幅度、频率、纵向分量都会按它缩放。
        /// </summary>
        public float amount;

        /// <summary>
        /// transfer（= 本体 &lt;transfer&gt; 的 source）：源 zone。留空表示「组件原位」或按 target 指定。
        /// 写成数组时表示「从这几个 zone 各取 quantity 件」（例如三种宝石各取一枚）。
        /// </summary>
        public List<string> source;

        /// <summary>transfer（本体 &lt;transfer&gt;.quantity）：从每个 source zone 搬几件（0 = 未指定，按 1 处理）。</summary>
        public int quantity;

        /// <summary>
        /// transfer / create / stack 的**目标 zone**（本体 &lt;transfer&gt;.destination）。
        ///
        /// 与 <see cref="zone"/> 分开是刻意的：`zone` 是**选择器**（"这个区域里的全部"，
        /// 用于 highlight / shuffle），`destination` 是**转移的终点**。以前两者共用一个字段，
        /// 同一份数据里"zone"一会儿是目的地、一会儿是选择范围，读起来要靠动作去猜。
        /// </summary>
        public string destination;

        /// <summary>
        /// transfer：把这一组当作**一个整体**搬运（同一段位移、同一时刻），而不是逐件飞。
        /// 用于「整摞牌堆从盒子里出来」：逐件飞会让一摞牌像扇形散开。
        /// </summary>
        public bool group;

        /// <summary>transfer 时若 &gt;=0：从该透明度淡入到 1（用于「发牌前不可见」）。</summary>
        public float fade_in = -1f;

        /// <summary>
        /// 这个动画事件在规则上是哪个本体事件（`&lt;top_draw&gt;`、`&lt;ontology::shuffle&gt;` …）。
        ///
        /// 「机制」与「语义」分开：原语说的是**怎么动**（transfer / flip / shuffle），
        /// `realizes` 说的是**这一动在规则里是什么事**。校验器会用本体的继承链检查它
        /// —— 例如发牌是 `&lt;top_draw&gt;`，而 `&lt;top_draw&gt;` 继承 `&lt;transfer&gt;`，
        /// 所以它能挂在 transfer 原语上；写个 `&lt;ontology::shuffle&gt;` 就会被拦下。
        /// 表现层原语（fade / highlight / scale / wait / showbox）没有对应事件，不要写。
        /// </summary>
        public string realizes;

        /// <summary>
        /// transfer：用**本体语言**指定要搬的是哪一类组件（见 <see cref="ConceptRef"/>）。
        ///
        /// 与 <see cref="template"/> 的分工：`what` 说的是"规则上这是什么"
        /// （一级发展卡、绿宝石、贵族），`template` 说的是"用哪张素材"
        /// （样本卡 / 垫牌 / 真卡），只在 create / stack 这类**本体里没有对应事件**的
        /// 实现层动作用。迁移完成后 transfer 只写 what。
        /// </summary>
        public ConceptRef what;

        /// <summary>
        /// 脚本里**有没有**用本体语言点名（`what.concept` 非空）。
        ///
        /// 别用 `what != null` 判断这件事：JsonUtility 会给嵌套的可序列化类字段
        /// **自动造一个空实例**（`what` 非 null、`concept` 为 null），于是"没写 what"
        /// 与"写了空 what"在代码里长得一模一样。2026-09 的 bug 就是它：
        /// 没写 what 的 transfer（"从这三个 zone 各取一枚"）被当成"概念为空"直接报错、
        /// 一件都不搬 —— 画面上什么都不发生，而脚本看起来完全正常。
        /// </summary>
        public bool HasWhat => what != null && !string.IsNullOrEmpty(what.concept);

        /// <summary>
        /// 实现层：确切的模板 id。**transfer 不要用它** —— 写模板名就把本作专用素材
        /// 写进了动画数据，换游戏/换素材就得重写。create / stack / destroy 的过滤用它。
        /// </summary>
        public string template;

        public float stagger;

        /// <summary>
        /// 终态：要把它设成什么状态值（本体 &lt;state_change&gt;.to / &lt;flip&gt; 的同一个词）。
        ///
        /// 现在只用 face：`face_up` / `face_down`。transfer / create / stack 都可以带 ——
        /// 意为「搬过去（创建出来）之后正面朝上」。
        ///
        /// **为什么不再用布尔 `flip`**：布尔只能表达「取反」，而取反的规则是"谁最后执行谁赢"
        /// —— 历史上正是它造成"播完是对的、换 cue 重建后又翻回背面"，查了一整天。
        /// 写终态就没有这个问题：`to: "face_up"` 无论当前是什么状态，结果都一样。
        /// </summary>
        public string to;

        /// <summary>
        /// <summary>
        /// `action: "zone"` 用：`add`（把 `zone_defs` 里的某个区域开出来）或 `remove`。
        /// 留空 = `add`。
        /// </summary>
        public string op;

        /// <summary>`action: "zone"` 用：`zone_defs` 里第几个实例（0 = 定义自己的 id）。</summary>
        public int index;

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

        // ---- point（指示物：箭头/圈/禁止/叉）----
        /// <summary>
        /// point：指哪一块**部位**（StageTemplate.part_anchors 的 id）。留空 = 指件中心。
        /// 这是动画层的"局部"概念：本体只说这张牌"印着声望值"，这里说它在牌面的哪儿。
        /// </summary>
        public string part;

        /// <summary>
        /// point：画什么形状 —— `arrow`（箭头）/ `circle`（圈）/ `forbid`（禁止：圈 + 斜杠）/
        /// `cross`（叉）。四种都是程序化生成的贴图，不需要美术素材。
        /// </summary>
        public string indicator;

        // ---- highlight ----
        /// <summary>高亮峰值透明度；负数 = 未指定（用默认）。</summary>
        public float peak_alpha = -1f;

        /// <summary>高亮放大倍率；0 或负数 = 未指定（用默认）。</summary>
        public float grow;
    }
}
