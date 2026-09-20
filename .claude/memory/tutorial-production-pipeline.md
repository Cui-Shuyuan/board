---
name: tutorial-production-pipeline
description: 讲规动画下一阶段路线——口播稿主/动画从、full 先做、quick 为店里默认、编译期起始画面、TTS 冻结后动画；Splendor full.lrc 已拆
metadata:
  type: project
---

# 讲规动画制作路线（2026-09-13 讨论定稿）

## 背景

基础问答已跑通，结构化规则足够支撑 LLM 编写新游戏。下一阶段重心转向讲规动画的自动制作。目标不是运行时 LLM 生成动画，而是把口播稿、TTS、动作和素材编译成静态数字资产，播放端只做确定性播放。

指导文档：`tutorial/下一阶段工作指导.md`

## 核心决策

- **口播稿是主，动画是次。** 动画配合口播节奏和分区，不反过来决定口播。
- **口播先行，TTS 冻结，动画后置。** 口播定稿并生成 TTS 后，音频时长是动画硬边界；动画时间不够只能在 TTS 冻结前增加留白。
- **一份规则事实，多个讲解版本。** quick / full 共享组件、资产和动作行为，不共享口播文本。
- **播放单元按 1～3 句口播切。** 不按规则小节切；规则小节只作为 group/导航层。
- **运行端不跟踪历史状态。** 播放期间只维护当前播放单元（叶子）；任意跳转由编译期生成的每段「起始画面」实现。
- **LLM 只填动画参数，不写新播放代码。** 沿用固定原语集（当前 12 个，见 [[tutorial-data-layer]]），不在 Unity 里为单个动画新写协程。
- **不做 authoring 界面。** 以 JSON + schema + validator + Unity 播放为准。
- **试点为《璀璨宝石》。** 以 `doc/splendor/口播稿.md` 的 full 版为标杆，先做 full，再抽出 quick。

## full 与 quick

- `full`：规则全覆盖，面向想深究的客人、店员或「老学究」，作为后续游戏口播稿标杆。
- `quick`：桌游店默认首讲版本。目标是让客人最短时间先玩起来，没讲到的细节由随时问答兜底。
- quick 不是 full 的加速朗读，需要重写口播；动作内容和资产尽量复用，时间轴按各自音频分别编译。
- 长期应先设计 quick，再扩展 full；当前手头只有 full，因此先用 full 建立结构和基准。

## 数据分层演进

| 层 | 内容 |
|---|---|
| L0 规则层 | `concepts.json` / `flow.json` |
| L1 口播脚本层 | 播放单元树、narration、source_refs、quick/full 标记 |
| L2 动作库层 | 可复用的语义动作步骤（发牌、拿宝石、购买、保留、贵族结算等） |
| L3 资产层 | 版图、slot、sprite、音频 |
| L4 编译产物 | 运行时 tutorial 数据，含每个叶子的起始画面和时间轴 |

源数据不手写起始画面，编译器离线从确定性时间轴生成。

## 动画 = 维护组件状态；运行时**要**跟踪状态（2026-09-15 修正）

用户定稿的模型：**动画就是维护一组组件的状态**。组件状态 = 它在哪个 zone、以什么姿态。
一切动画都是「组件从 source zone 移动到 destination zone」，世界坐标由 zone 的布局规则推导，
动画数据里不写坐标。

由此对上一节「运行端不跟踪历史状态」的修正：

- **运行时维护状态**（哪个组件在哪个 zone 的第几位）。不维护就无法实现「宝石从供应堆
  飞过来」——供应堆里得真的有那三枚。
- **顺序播放**：一条 cue 接着上一条 cue 的终态继续。
- **重播当前 cue**：恢复到这条 cue 的**入口状态**（入口快照，不重建对象）。
- **任意跳转**：仍由编译器离线复算每个 cue 的入口状态写进 runtime（L4），
  运行时加载「入口状态 + 本节时间轴」从 0 播放。所以是「运行时维护状态 + 编译器负责跳转」，
  而不是二选一。

为什么不再手工摆 slot：zone 是**逻辑容器**，组件的落点是 zone 布局算出来的，
所以「改桌面布局」是改一处数据，而不是改每一条动画。

## 数据三层分工

| 层 | 文件 | 只负责 |
|---|---|---|
| 语义事实 | `games/{game}/flow.json`、`concepts.json` | 源、目的地、对象、数量 |
| 视觉绑定 | `anim/_stage/{game}.table.json` | 语义区域画在屏幕哪里（哪些 zone、颜色分几堆） |
| 时间 | `anim/{track}.json` 里那条 cue 的 `events` | 第几秒发生、强调、错峰 |

**flow 只当查阅资料，不做运行时解析（2026-09-15 用户裁决）**：flow / concepts 里的
`source` / `destination` / `quantity` 由 AI 在**编写动画脚本时自己读**，然后把结论写进
cue 的动画数据。理由：这是一次性工作，读一次做好就可以一直用；为它写一个 JSON 解析器
和语义运行时层，等于把一次性劳动变成永久维护的代码。
（曾实现过 `MiniJson.cs` + `SemanticMap.cs` + `transfer` 原语，随后按此裁决删除。）

示例：`take_gems_different` 在 concepts 里已经是
`<gem_supply>` → `<ontology::player_holding>`、3 颗，所以 cue 直接写
`gem_supply_diamond/ruby/sapphire → player_holding`，不再重复推导。

## 任意跳转与起始画面

客人可以像看视频一样任意跳转。实现方式：

- 编译器为每个叶子离线计算开头所有组件的完整状态（zone + 姿态）；
- 运行时加载「当前叶子入口状态 + 本节时间轴」并从 0 播放；
- 不重放前序章节，不维护全局历史；
- 洗牌等随机效果必须确定性化（固定种子或预生成结果）。

这是「运行时维护状态」与「编译期静态资产化」的结合：跳转的代价前移到编译期，
播放期间的连续状态由运行时自然维护。

## 打断问答

- 打断时暂停音频和动画。
- 问答只带当前叶子上下文：叶子 id、路径标题、口播全文、source_refs。
- **不传播放秒数**，避免半句话语义不完整；现有 `/api/chat` 可加可选 `tutorial_context`。
- 回答后重播当前叶子，从起始画面和 0 秒开始。

## 试点当前状态

- 已将 `doc/splendor/口播稿.md`（full）拆成 `games/splendor/tutorial/full.lrc`：59 个播放单元、16 个导航分组，行格式 `[mm:ss.xx][id:...][ref:...]台词`。
- 时间是 TTS 前估算，`[timing:estimated]`；TTS 冻结后重写为 `[timing:tts]`。
- LRC-like 格式说明见 `tutorial/README.md`，解析/校验器为 `scripts/validate_timed_script.py`。
- 待用户 review 拆分、台词和 source refs；quick 版及 core/extended/flavor 标记留到 quick 阶段。
- TTS 已选用火山豆包语音合成 2.0（WebSocket 双向流式接口）。实现脚本：`scripts/tts_doubao.py`；协议模块：`scripts/volcengine_ws_protocols.py`；依赖：`scripts/requirements-tts.txt`。
- full 版 109 条 cue 已全量合成并试听确认，输出 `*.mp3` + `*.subtitle.json`（含字级时间戳）+ `tts_manifest.json`；`full.tts.lrc` 已生成。
- 运行时数据编译器：`scripts/build_tutorial_runtime.py`；产物 `games/splendor/tutorial/full.runtime.json`（cue 顺序、音频、时长、字幕、group_path、refs）。
- Unity 纯音频播放器 v0：`client/Assets/Scripts/Tutorial/TutorialCuePlayer.cs`，支持播放/字幕/跳转/上下段/暂停/重播当前 cue；默认关闭旧 `TutorialDirector` 的自动搭景，待进 Unity 实测。
- 下一步：组件扫描、slot 标定、单 cue 动画 pilot、打断问答接线。
- **分层编辑流程已加入**：编辑源 `games/{game}/tutorial/script.{track}.json`（group_path -> cue -> beat）；`scripts/tutorial_script_tool.py` 支持 import/build/validate/split/merge/set-pause；`scripts/rebuild_tutorial.py` 一条命令跑完 source -> LRC -> TTS -> runtime；`tts_doubao.py` 新增 `--force` / `--prune`。

## 讲规动画的生产闭环：脚本 → 动画 → 对账（2026-09 跑通）

**一个动画一个文件**：`games/{game}/tutorial/anim/{track}.json`（舞台绑定在 `anim/_stage/*.json`）。
每条 cue 里同时写着 **`events`（动画）** 和 **`enter`/`exit`（契约 = 用程序语言写的头尾两帧）**。

三条命令构成一轮：

```bash
./scripts/sync_workspaces.sh from-linux    # ① 数据推到 Windows（Unity 读的是 D:\workspace\board）
./scripts/dump_states.sh                   # ② 一次 Unity 启动，整条轨道播到尾，每条 cue 记终态
python3 scripts/check_cue_script.py --all  # ③ 对账：终态 vs 契约（+ 跨 cue 链 + 取景）
```

外加两条**不需要采样**的静态检查（改完数据随手跑，秒级）：

```bash
python3 scripts/validate_cue_anim.py     # 字段归属、本体概念、取景链、契约覆盖面
python3 scripts/check_unity_scripts.py   # Unity 侧 C# 能不能编译（含 Assets/Editor）
```

**采样文件是生成物、不进版本管理**（`anim/*.exitstate.json`）：每次采样都会改它，
跟踪它就会把工作区弄脏，然后下一次同步被自己的产物挡住。

**三层要分清**（2026-09 一个 bug 查了半天，就因为只看了前两层）：

| 层 | 谁看 | 例子 |
|---|---|---|
| 契约/脚本**文件** | 人、`validate_cue_anim.py` | `full.json` 里写的是什么 |
| 引擎**解析到的对象** | `-dumpEvents 1` | JsonUtility 把没写的 `what` 变成空实例 |
| **采样**到的状态 | `DumpState` + `check_cue_script.py` | 桌上实际有几件、哪面朝上 |

**当前成绩**（2026-09，17 条已做动画的 cue）：对账 **0 处不一致**；
12 条取景警告（都是"供应区特写里必然出现发展卡市场与一级牌堆"，待用户裁决：改取景 or 在契约里声明）。

## 下一款游戏的推荐流程（2026-09-21 用户定调）

Splendor 的教训：

- 动画做到一半才开始回头找口播语义 → 出现“状态收尾挂到下一个语义无关 cue”的旧账
  （宝石 7→4 被塞进贵族介绍）。

- 组件介绍没有一开始就独立成树 → 后面不停补 overlay/独立 world。

因此下一款游戏按这个顺序做，**不要从已有动画半成品往回修**：

1. **先写口播稿**：只产出 `doc/{game}/口播稿.md`，不写 stage、不写 `full.json`。
2. **再切稿**：切成 1～3 句的 cue（`script.{track}.json` / `{track}.lrc`），先冻结节拍。
3. **先写“文字版动画”骨架**：每条 cue 先写
   `story`（念什么）+ `note`（为什么这么演）+ `tree`（哪棵树/是否切树）
   + `enter/exit`（画面要变成什么）+ `camera`；**这一轮先不写 events**。
4. **先用 BoardAI API 检查正确性**：把文字意图逐条变成最小合法性问句，规则错了在这一步就发现；
   不要等动画做完再验。
5. **再搭树/舞台**：根据文字里的主体设计 world/zone/extent；
   **组件介绍默认独立 world，场上天然只有该组件**。
6. **再写 events**：只把第 3 步的契约翻译成原语，不得改变契约语义；
   任何状态清理必须归到口播真正说它的那条 cue，禁止挂到语义无关的下一条。
7. **最后对账/验收**：`validate_cue_anim` + `validate_anim_rules` + `dump_states`
   + `check_cue_script`，再给用户做视觉验收。

一句话：**先有文字脚本，再有结构，再有动画；BoardAI API 在动画之前当独立裁判。**

## 相关记忆

- [[tutorial-module]] — 第五阶段技术选型与 Unity 现状
- [[tutorial-data-layer]] — 现有 `tutorial.json` schema / validator / 原语集
- [[splendor-progress]] — 试点游戏规则与 flow 现状
- [[user-preferences]] — 程序确定性、数据驱动、截图视觉迭代

**Why:** 这次讨论确定了下一阶段讲规动画的工作重心和关键架构：口播稿主导、full/quick 双版本、编译期起始画面、TTS 冻结后动画。
**How to apply:** 任何讲规动画实现讨论先看 `tutorial/下一阶段工作指导.md`；下一会话从 full 口播稿结构化拆解开始，不直接写动画。
