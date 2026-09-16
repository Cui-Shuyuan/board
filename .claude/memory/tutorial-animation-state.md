---
name: tutorial-animation-state
description: 讲规动画定稿模型——动画=维护组件状态(zone+姿态)，运行时维护状态、重播回入口快照、跳转由编译期复算；flow 只当查阅资料不写运行时解析器；stage 只管视觉绑定
metadata:
  type: project
---

# 讲规动画：状态 / zone 模型（2026-09-15 用户定稿）

## 核心命题（用户原话）

> 所谓的动画，其实就是维护一组组件的状态。更具体一点说，在什么位置，以什么姿态。

> 从一开始我们就已经定义好了公共供应堆，这部分放宝石，那部分放牌堆。然后在 set 的时候，
> 这些宝石就从镜头外飞到了供应堆中，拿取，则是从供应堆移动至玩家保留区，这些都是最简单
> 不过的移动函数，如果设计得好，应该只需要填一个 source 和 destination 就够了。

由此推出：

1. **组件状态 = (zone, 顺位) + 姿态。** 世界坐标由 zone 的布局规则**推导**，
   动画数据里**不写坐标**。
2. **所有动画都是 zone → zone 的移动。** 设置阶段「从盒子里拿出来放进供应堆」和游戏阶段
   「从供应堆拿到持有区」是同一个操作，只是源 zone 不同（`offstage` 就是画面外的源 zone）。
3. **搬走要收拢。** 同一 zone 里排在后面的组件顺位前移，视觉上堆真的少一枚，而不是留空位。
4. **zone 是持久的逻辑容器。** 「公共供应堆」这类事实在设置阶段就确立，之后只是从中搬运。

## 运行时要维护状态（修正 2026-09-13 的「不跟踪历史」）

| 场景 | 做法 |
|---|---|
| 顺序播放 | 一条 cue 接着上一条 cue 的**终态**继续 |
| 重播当前 cue | 恢复到这条 cue 的**入口快照**（不重建对象） |
| 任意跳转 | 编译器离线复算每个 cue 的入口状态写进 runtime |

所以不是「运行端不跟踪历史」，而是**运行时维护连续状态 + 编译器负责跳转所需的入口状态**。
参见 [[tutorial-production-pipeline]]。

## 数据三层分工

| 层 | 文件 | 只负责 |
|---|---|---|
| 语义事实 | `games/{game}/flow.json`、`concepts.json` | 源、目的地、对象、数量 |
| 视觉绑定 | `games/{game}/tutorial/anim/_stage/{game}.table.json` | 语义区域画在屏幕哪里、颜色分几堆、模板外观、开局摆放 |
| 时间 | `games/{game}/tutorial/anim/{track}/{cue_id}.json` | 第几秒发生、强调、错峰 |

**cue 只写 zone，不写坐标也不重复 flow 的语义。**

```json
{ "at": 3.30, "dur": 0.55, "action": "move",
  "from": "gem_supply_diamond", "zone": "player_holding", "easing": "easeInOutCubic" }
```

## flow 只当查阅资料，不做运行时解析（用户裁决）

> 其实你不必写一个解析器专门去读 flow 的 transport 信息，我可以接受你只是自己去看，
> 然后再自己去写动画脚本。因为这个其实是一次性的工作，一次做好，可以永远用。

- **AI 在编写动画脚本时自己读 flow / concepts**，把 `source` / `destination` / `quantity`
  的结论写进 cue 数据。
- **不要**为此写 JSON 解析器和语义运行时层。曾实现过 `MiniJson.cs` + `SemanticMap.cs`
  + `transfer` 原语 + stage 的 `visual` 绑定段，按此裁决全部删除（commit `c295c41` 之后回退）。
- 例外只在真正需要程序确定性复算时再考虑：例如设置阶段 `distribute_gems` 的
  `quantity_per_color` 随人数变化，将来若要做「按人数参数化的一整套设置动画」，
  那时再评估是否值得程序化，而不是现在预先建层。

写动画脚本前要查的 flow 事实（璀璨宝石首批）：

| flow/concepts 节点 | source | destination | 数量 |
|---|---|---|---|
| `take_gems_different` | `<gem_supply>` | `<ontology::player_holding>` | 3（≥3 色时） |
| `take_gems_same` | `<gem_supply>` | `<ontology::player_holding>` | 2 |
| `distribute_gems` | `<ontology::game_box>` | `<gem_supply>` | `quantity_per_color` 按 2/3/4 人 = 4/5/7 |
| `distribute_gold` | `<ontology::game_box>` | `<gold_supply>` | 5 |
| `prepare_level_N_deck` | `<ontology::game_box>` | `<development_deck_level_N>` | all |
| `deal_card_market_level_N` | `<development_deck_level_N>` | `<card_market>` | 4，`slot: level_N` |
| `deal_nobles` | `<ontology::game_box>` | `<noble_market>` | `player_count + 1` |
| `give_starting_player_marker` | `<ontology::game_box>` | 起始玩家 | 1 |
| `purchase → pay_cost` | `<ontology::player_holding>` | `<gem_supply>` | 卡面 cost 减 discount |
| `purchase → play_card` | `<card_market>` | `<ontology::development_area>` | 1 |
| `reserve_from_market` | `<card_market>` | `<ontology::hand>` | 1 |
| `refill_market`（top_draw） | `<development_deck>` | `<card_market>` | 1 |

注意 concepts/flow 的键名带命名空间前缀：`<ontology::transfer>`、`<ontology::top_draw>`。

## Unity 侧实现（client/Assets/Scripts/Tutorial/）

- `ZoneStore.cs` — 占用账本。`MoveTo` 重排剩余组件的 Order。
- `TutorialCueAnimPlayer.cs` — 渲染层（每个组件按推导位置生成 sprite）+ 时间轴层
  （时钟取 `audioSource.time`）。支持 `stagger`（同组组件错峰触发）。
- `TutorialCueAnimData.cs` / `CueAnimActor.cs` / `Palette.cs` — 数据模型与渲染壳。
- `TutorialCuePlayer.cs` — cue 播放器，`B` 键跳到当前在做的动画切片。

## 相关记忆

- [[tutorial-production-pipeline]] — 制作路线总纲（本文件修正其「不跟踪历史」一节）
- [[tutorial-data-layer]] — 早期 `tutorial.json` 合约（已被本模型取代）
- [[tutorial-module]] — 第五阶段技术选型
- [[user-preferences]] — 程序确定性、一次性工作不引入永久维护成本

**Why:** 这次讨论把动画从「每条 cue 描述一整张画面」扭转为「一张持久牌桌上维护组件状态」，
并明确了 flow 的定位（查阅资料而非运行时依赖）。前一种做法会让 100+ 条 cue 各自复制牌桌事实，
必然漂移；后一种做法里牌桌只描述一次。
**How to apply:** 做任何讲规动画前先确认三件事——牌桌（zone/模板/开局）写好了吗、
这条 cue 的 flow 事实查到了吗、数据里有没有混进坐标或重复的语义。

## 动画 = 时间的函数（2026-09 重构）

**核心决定**：动画不靠「每帧推进一点」累积，而是 `Seek(t)` 直接采样算出画面。

理由不是优雅，是**可验证性**：批处理环境（`-executeMethod`）没有帧循环，
`Time.deltaTime` 恒为 0、`StartCoroutine` 的协程不会推进、`Time.captureDeltaTime`
也无效。因此只要动画依赖帧推进，就**无法离屏验证**，只能靠人肉截图——
这个盲区导致连续多轮「我这边通过、用户那边不对」。

重构后：
- 事件触发时，**逻辑状态立即到终态**（Store 是时间的阶跃函数）
- 视觉过渡登记为 `Clip`，由 `SampleClips(t)` 采样：位置/翻转/缩放/透明/洗混全部如此
- 暂停、跳转、倒放天然正确，不需要特判
- `CaptureTimeline` 可按任意步长逐帧出图，离屏就能看到完整动画

### 自检入口
```
Unity.exe -batchmode -projectPath <client> \
  -executeMethod BoardGameTutorial.Editor.TutorialFrameCapture.CaptureTimeline \
  -captureCue setup.cards.002.1 -captureFrom 4.0 -captureTo 7.6 -captureStep 0.2 \
  -logFile <log>
```
输出到 `client/CaptureOut/timeline/t<毫秒>.png`。

## 反复踩过的坑（都属「静默失败」）

| 现象 | 真因 |
|---|---|
| 字段永远读不到 | `JsonUtility` 不支持 `int?`/`float?`，静默留 null |
| move 从不执行 | `from` 写成字符串，而模型是 `List<string>`，静默丢弃 |
| 牌堆里出现宝石 | `from`+`take` 按位置取件，盒子里宝石排在卡片前面；需用 `template` 筛选 |
| 牌停在原地 | 补间终点取了「当前 zone」位置，而牌的归属还在盒子 |
| 牌被后续事件拉回起点 | 只动画面、不记录格位；逻辑归属必须同步落到目标格 |
| 牌堆看似正面朝上 | 停在牌堆上的市场牌未标记 `Flipped`，正面盖住了卡背 |

**规律**：这些都不报错，只表现为「画面不对」。所以每遇到一种，
都要在 `SelfTest` / 校验器里补一条对应断言。

## 入口状态是一棵树（2026-09）

**核心规则**：每条 cue 的入口状态由它自己声明的 `entry` 决定。

```
entry 未写      → 继承「上一条有动画数据的 cue」的终态（顺序播放的默认）
entry = "initial" → 牌桌初始状态
entry = "<cue id>" → 那条 cue 的终态
```

### 为什么需要它

对照教学的两条 cue 必须是**兄弟**，不是父子。例如：

```
        玩家区（0 张）
        ├── cue A「可以抽一张」：1 张 → 保留区 + 对勾
        └── cue B「不可以抽两张」：2 张 → 保留区 + 叉
```

如果 B 以 A 为父，演示 B 时手里已经有 A 留下的 1 张，画面就错了。
两条都声明 `entry: "initial"`（或同一个共同祖先），各自从同一张桌子出发。

### 实现

- `TutorialCuePlayer.ResolveEntryCueId(index)`：解出入口来自哪条 cue。
  没有显式声明时，**跳过没有动画数据的 cue**（它们不改牌局）继续往上找。
- `TutorialCuePlayer.ApplyEntryState(anim, entryCueId)`：
  用**独立**的草稿播放器，从 `stage.initial` 起沿祖先链把每条推到终态，
  再把结果整体交给正式播放器。
- 顺序播放下一条时走快捷路径（直接用当前画面），不重新解。

### 踩过的坑

| 现象 | 原因 |
|---|---|
| 牌堆消失、发牌方向反了 | 独立重建实例**没把组件交接过来**（只复制了 zone/order） |
| 牌背叠在牌堆上 | 重放时 `start.set` 又生成一份；且隐藏逻辑漏了「无片段」的分支 |
| 片段累积到 90+ | `clips` 只在回退时清空，换 cue 从不清理 |
| 市场牌与牌堆数量核对错乱 | `CountIn` 只判 palette，而市场牌与牌堆共用 `card_level_1` |

**共同规律**：这些全都只在「连续播多条 / 跳转」时才出现，单条 cue 的隔离测试永远看不到。
所以自检必须走真实路径（`SelfTestEntryTree` / `SelfTestLivePath` / `CaptureSequence`）。

## 洗混是通用方法（2026-09）

任何 zone 都能洗，只要在 cue 里写一条事件：

```json
{ "at": 0.5, "dur": 0.7, "action": "shuffle", "zone": "deck_level_1" }
{ "at": 0.5, "dur": 0.7, "action": "shuffle", "zone": "gem_supply_diamond", "amount": 0.6 }
```

`amount` 是强度倍率（省略或 0 = 1.0），用于小棋子或大牌堆的差别。

### 实现要点（都在一处，改这里就够）

`TutorialCueAnimPlayer.Shuffle` 内部类集中了全部参数：

| 参数 | 值 | 说明 |
|---|---|---|
| `AmpMin/AmpMax` | 4.0 / 6.2 mm | 水平幅度区间 |
| `FreqMin/FreqMax` | 8 / 14 Hz | 抖动频率区间（接近人手搓牌） |
| `DepthMin/DepthMax` | 0.25 / 0.60 | 纵向幅度占水平的比例 |
| `EnvelopePower` | 0.45 | 包络形状（前 1/4 起振、中段保持、末 1/4 收住） |

**每张牌的四个量都由组件 id 散列决定**（`StableHash` + `Hash01`，不用 `Random`）：
幅度、频率、初相、纵向分量。所以同一瞬间有的向左有的向右、抖得也不一样快。
若所有牌同相摆动，看起来只是整摞在平移，不像洗牌。

### 断言

`SelfTestShuffleGeneric` 检查三件事：
1. 三个牌堆都被洗到（张数 40/30/20）
2. **同一 zone 内每张位移互不相同** —— 实测 40 张里 39 种（98%）
3. 同一方法可用于非牌堆 zone（宝石供应堆）

第 2 条是核心：它把「各张独立抖动」这个手感变成了机器可查的性质。

## 选择器规则：单件 vs 整组（2026-09）

**模型始终是平的：一张牌就是一个 item。** 牌堆不是"一个对象"，
只是 Store 里 40 个 item 恰好都归属 `deck_level_1`。
「整体」只存在于**操作层面**：操作可以作用于单个 item，也可以作用于一组。

事件用选择器指定作用范围，**全操作统一**：

| 写法 | 含义 |
|---|---|
| `target: "card_back_1#5"` | 单件 |
| `zone: "deck_level_1"` | 整组（该 zone 内全部） |
| 两个都写 | **数据错误**：报警告，以 `target` 为准 |

### 为什么这样分

- **发牌**要精确到单张 → 用 `target`（或 `move` 的 `from`+`slot`）
- **洗混 / 高亮一整摞** → 用 `zone`，一次说清"这一组"
- 两者不需要两套模型；同一份扁平数据既能单张操作，也能整组操作

### 整组操作怎么算

以**组内重心**为锚点，而不是某张牌的位置：

```
每张牌的位置 = 重心 + (原位 - 重心) × g
每张牌的缩放 = 基准缩放 × g
```

这与"把整组当作一个对象缩放"等价，且不要求组是规则形状 ——
牌堆（斜向错开）、宝石堆、玩家持有区都能用。

### 踩过的坑

| 现象 | 原因 |
|---|---|
| 高亮只让堆底那一张大了一圈 | 事件写了单张 `target`，走了单件脉冲 |
| 一级修好了，二三级没有 | 另外两条 cue 还是单张写法（断言也只验了一级） |
| 同一个事件在两类操作里选中不同东西 | `Resolve` 是 zone 优先，`highlight` 是 target 优先 |
