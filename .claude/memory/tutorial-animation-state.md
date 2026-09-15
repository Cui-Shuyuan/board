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
