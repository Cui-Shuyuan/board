---
name: civolution-rules-notes
description: 文明演化规则学习笔记与 piece 表设计草案（截至 2026-07-20）
metadata:
  type: project
---

# 文明演化规则学习笔记

## 资料来源
- 口播稿：`games/civolution/口播稿.md`
- 卡牌图片：`games/civolution/card/神权制.jpg`
- 英文规则书：`games/civolution/Civolution_Rules_US_web_v1_0.pdf`（来自 BGG，被视为权威来源；已由 `pdftotext` 提取为同目录 `.txt` 文件）

## 核心机制（已确认）

### 游戏结构
- 支持 1–4 人，多人为主；本笔记聚焦多人规则
- 4 个时代（era）× 8 个阶段（phase）：
  1. **New cards**（新卡牌）：翻开 1 张事件牌 + 5 张研究牌（分别覆盖各自明面堆）
  2. **New goals**（新目标）：按座位顺序可选 1 枚目标芯片，然后补满展示区
  3. **Extra find**（额外收获）：按座位顺序，每位玩家在有部落的陆地区域拿取 1 个该类型材料到自己的仓库
  4. **Action phase**（行动阶段）：主要阶段，玩家轮流激活模组或执行 Reset
  5. **Site phase**（地点阶段）：结算狼谷、冰川、神秘橡树效果
  6. **Feeding phase**（喂养阶段）：喂部落 → 站立部落得分
  7. **Event phase**（事件阶段）：天气效果 → 事件效果 → 时代计分 → 确定新的起始玩家
  8. **Income phase**（收入阶段）：执行收入芯片 → 雕像计分 → 移除狩猎标记 → 补充重置标记

### 关键概念
- **区域（Territory / Region）**：大陆板块上的连续同色部分；相邻板块的同色区域仍算不同区域
- **地形类型（Territory types）**：Forest（森林）、Grassland（草原）、Hills（丘陵）、Swamp（沼泽）、Mountains（山脉）、Desert（沙漠）、Water（海洋）
- **营地（Encampment）**：公共版图上的圆；黄色火边营地=fire encampment，站上去得分数
- **地点（Site）**：独立板块，不能站人，背面朝上放置，通过 Exploration 模组翻开
- **部落（Tribe）状态**：strong（站立，upright）/ weak（躺倒，lying）；只有 strong 部落能迁徙，weak 部落在喂养阶段不喂会死亡
- **骰子**：activation dice（白骰子，6 面）、fate dice（粉骰子，6 面）
- **标记（Marker）**：创意标记（idea marker）、计划标记（planning marker）、焦点标记（focus marker）
- **特征（Feature）**：6 种特征标记，放在个人面板的特征格上；英文规则书目前明确提到 Intelligence、Dexterity，其余待从图例/词汇表确认
- **进程轨（Progress tracks）**：5 条 — Technology、Prestige、Knowledge、Construction、Culture；每条 0–12 格，推过头后每步改为 2 分
- **阿格拉恩惠轨（Favor of Agera track）**：影响恩惠检定（favor test）
- **仓库（Storage area）**：`<storage_area>` 已改为继承 `<player_holding>`——其中的材料已经属于玩家，只是尚未消耗；它不是 `<public_supply>` 或 `<player_supply>`。

### 公共版图组成

公共版图不是单一组件，而是由**多个 aid 拼成的整体**：

| 组件 | ontology 归类 | 说明 |
|---|---|---|
| **4 frame pieces 拼成的 score track 外框** | `<public_board>` 的一部分（aid） | 物理外框属于公共版图，逻辑上 `maps_to` `<score_track>`（zone） |
| **8 continent tiles** | `<continent>`（zone） | 7 块 2×1 + 1 块 1×1，拼成大陆；是公共游戏区域，不属于公共版图 aid |
| **24 sites** | `<tile>`（地点板块） | 背面朝上放在 continent 的凹槽中 |
| **Progress board（进程版图）** | `<public_board>` | 左侧大板，含 9 个功能区 |
| **Sequence board（流程版图）** | `<public_board>` | 右侧大板，含阶段序列、天气轨等 |

#### 进程版图（Progress board）9 个区域

| 区域 | 英文 | 用途 |
|---|---|---|
| a | Hunting table | 狩猎时查表：地形 + 骰子点数 → 食物数量 |
| b | Progress tracks | 5 条进程轨：Technology、Prestige、Knowledge、Construction、Culture |
| c | Final scoring area | 终局计分区，按 9 个类别依次计分 |
| d1 | Hunting token display | 狩猎标记供应/展示 |
| d2 | Dice display | 白骰子/粉骰子供应/展示 |
| d3 | 100-point token display | 100 分标记/阻挡标记供应 |
| d4 | Goal chip display | 蓝色目标芯片展示 |
| d5 | Income chip display | 白色收入芯片展示 |
| d6 | Attribute chip display | 黄色属性芯片展示 |

#### 流程版图（Sequence board）5 个区域

| 区域 | 英文 | 用途 |
|---|---|---|
| e | Phase sequence | 阶段序列，六边形阶段标记指示当前阶段 |
| f | Weather gauge | 天气轨，天气标记移动决定天气效果 |
| g | Event card spaces | 事件牌格：背面朝上堆 + 正面朝上展示 |
| h | Era scoring area | 时代计分区，4 个时代各翻 1 张计分类别瓷砖 |
| i | Favor of Agera track | 阿格拉恩惠轨，影响恩惠检定 |

### Track 概念的 ontology 归属（已确定）

采取 **C 方案**：**`<track>` 作为 `<zone>` 的子类**，是一种「有序 zone」，其中的 `<marker>` 只做内部位置移动，不发生 zone 间 transfer。

- **`<token>`**：物理基类，所有小型计数/标记物的父类
  - **`<resource>`**：可被消耗/产出的 token（食物、材料、宝石等）
  - **`<marker>`**：用于指示状态/位置的 token
- **`<track>`**（extends `<zone>`）：存放 marker 的有序区域
- **`<score_track>`**（extends `<track>`）：分轨，记录玩家分数
- 其他轨道（progress tracks、Favor of Agera track、weather gauge、phase sequence）在游戏级别定义为 `<track>` 子类，不入统一本体

```
State
├── 玩家拥有 N 个 <score>（玩家分数状态）
├── 玩家拥有 N 个 <food>（食物状态）
├── 5 条 progress track 位置
├── Favor of Agera 位置
└── 天气/阶段位置

Resource（extends Token）
├── Score（胜利点数/分数）
├── Food
├── Money
├── 各种材料
└── ...

Token
├── Resource
└── Marker
    ├── score_marker
    ├── progress_marker
    ├── phase_indicator
    ├── weather_indicator
    └── favor_marker
```

### 模组（Modules）
- 个人面板有 **22 个模组**：15 个主模组（main modules，可升级）+ 6 个特征模组 + 1 个睡眠模组
- 主模组：Migration、Procreation、Production、Transport、Sustenance、Exploration、Building、Planning、Research、Achievement、Insight、Mutation、Invention、Trade、Activity
- 升级：Level-I → 翻面到 Level-II → 取下露出 Level-III（Tile 被弃掉）
- 激活模组需使用恰好 2 个白骰子，其点数分别匹配模组左下、右下角的数值
- 可用 idea markers 修改骰子点数（1 和 6 相连）；planning/focus markers 可替代白骰子

### 研究牌（Research cards）三段式结构
所有研究牌安装到个人面板后，只有上半部分露出：

| 部分 | 性质 | 触发时机 | 是否可重复 |
|---|---|---|---|
| **上** | 活动能力（activity ability） | 玩家执行 Activity 模组时，作为可选活动之一 | 是 |
| **中** | 费用格（cost spaces，5 格） | 安装时一次性满足 | 否 |
| **下** | 即时奖励（instant bonus） | 安装成功后必须立刻结算，不结算则作废 | 否 |

五种研究牌类型（颜色）：
- **Building**（蓝色）
- **Insight**（黄色）
- **Mutation**（绿色）
- **Achievement**（紫色）
- **Invention**（棕色）

### 费用格（Cost spaces）规则
- 安装到第 L 层时，至少支付从左至右前 L 格中的所有非空费用
- 玩家可以选择**假设更高层**（assume higher stage），即多支付以获得更多费用格下方的分数；但必须连续满足到该层的所有费用格，不能只挑中间某几格
- 费用格类型：
  - **红色 -N + 资源/特征图标** = 从控制台/仓库返还 N 个对应标记到供应堆（真正支付）
  - **白色 N + 特征图标** = 必须持有至少 N 个对应特征标记（不消耗）
  - **替代条件**（多见于建造牌）= 如「已建造 2 个聚落」；满足其一即可
  - **包含关系** = 图标表示高数量条件自动满足低数量条件
  - **空** = 自动视为已满足（inactive）

### 安装规则
- 同种研究牌或芯片必须安装在同一列（白色收入芯片除外，可任意列）
- 只能安装在该列最下面的空闲位置
- 安装后位置不可移动
- 第 1 层填满：获得收入芯片并立即执行一次其效果
- 第 2 层填满：翻转 stage-4 或 stage-5 tile 到 active 面

### 进程轨奖励（Track bonuses）
每推过一条进程轨上的奖励线可获得：
1. 升级一个主模组（模组上的一个费用数值颜色需与轨道颜色匹配）
2. 推进阿格拉恩惠轨 1 格 **或** 触发一个收入芯片效果
3. 同 1
4. 3 分
5. 翻转 stage tile **或** 3 分
6. 3 分

### 地点（Sites）效果
已翻开地点在地点阶段结算：
- **Gorge of the Grimwolves（狼谷）**：相邻区域每有一个，需支付 1 食物
- **Glacier（冰川）**：相邻区域每有一个，需虚弱 1 个站立部落
- **Mystic oak（神秘橡树）**：相邻的水/沼泽/沙漠区域每个得 2 分，森林/草原/丘陵/山脉每个得 1 分
- **Hidden grotto（隐蔽洞窟）**：激活 Production 模组时，先在相邻区域生产并运输 1 个材料
- **Holy rock（圣岩）**：激活 Procreation 模组时，先在相邻区域过恩惠检定后繁育
- **Cave（洞穴）**：迁徙时，所有与任意洞穴相邻的区域视为彼此相邻
- **Mushroom valley（蘑菇谷）**：相邻区域狩猎额外 +1 食物
- **Volcano（火山）**：相邻区域不能建农场；翻开时相邻农场退回控制台
- **Building ground（建造点）**：用于建造雕像/聚落

## 已分析卡牌：神权制（Theocracy, C16）

- **类型**：洞悉牌（insight card）
- **活动能力**：支付 1 食物 → 选择 1 个自己的区域 → 在最多 2 个空置营地上各放置 1 个虚弱部落
- **费用格**：
  - 第 1 层：返还 1 个 Intelligence 特征标记到供应堆
  - 第 2 层：+ 持有 2 个 Expression 特征标记
  - 第 3 层：+ 持有 1 个 Calm 特征标记（待从图例确认英文名称）
  - 第 4–5 层：无额外费用
- **即时奖励**：Culture 进程轨推进 1 格
- **费用格下方分数**：无

## Piece 表设计草案

基于研究牌三段式结构，piece 表至少包含：

```json
{
  "id": "theocracy",
  "card_no": "C16",
  "type": "<insight_card>",
  "name": { "zh": "神权制", "en": "Theocracy" },
  "install_cost": {
    "slots": [
      { "payment": { "<intelligence_feature>": 1 } },
      { "possession": { "<expression_feature>": 2 } },
      { "possession": { "<calm_feature>": 1 } },
      null,
      null
    ],
    "slot_scores": [0, 0, 0, 0, 0]
  },
  "install_reward": {
    "mandatory": true,
    "forgoable": false,
    "effect": { "<track_advancement>": { "<culture_track>": 1 } }
  },
  "activity_ability": {
    "icon": "<activity>",
    "cost": { "<food>": 1 },
    "target": { "type": "<region>", "owner": "self" },
    "effect": {
      "place": {
        "<tribe_weak>": 1,
        "per_target": 1,
        "max_targets": 2,
        "location": { "type": "<encampment>", "state": "vacant" }
      }
    }
  }
}
```

### 设计选择说明
1. `install_cost.slots` 用数组下标表示层数，0 = 第 1 层；安装到第 L 层时满足 `slots[0..L-1]` 中非空项的并集。
2. `payment` vs `possession` 区分真正返还标记的消耗与仅检查持有的条件。
3. `slot_scores` 记录每格下方的额外分数，多支付时累计。
4. `install_reward.mandatory: true` 表示安装后必须立刻结算；`forgoable: false` 表示不能不结算（规则书：不能或不想执行的奖励作废；但神权制这种推进轨道通常无法「不想执行」？待进一步确认）。
5. `activity_ability` 为可选字段；并非所有研究牌都有活动能力（有些牌上部可能是被动持续效果或得分能力）。

## 与口播稿的术语对照/修正

| 口播稿 | 英文规则书 | 备注 |
|---|---|---|
| 智慧特征 | Intelligence feature | 费用格红色 -1 表示返还该特征标记 |
| 表达特征 | Expression feature | 白色数字表示持有数量 |
| 镇定特征 | （待确认，可能是 Calm/Composure） | 需看图例或词汇表 |
| 进程轨 | progress tracks | 5 条，Technology / Prestige / Knowledge / Construction / Culture |
| 阿格拉恩惠轨 | Favor of Agera track | 恩惠检定通过区间 |
| 地点 | sites | 包括 Building ground、Gorge、Glacier、Mystic oak 等 9 种 |
| 阶段标记 | phase indicator | 六边形标记 |
| 天气标记 | weather indicator | 在 weather gauge 上移动 |

## 待确认/待学习
- 6 种特征的完整英文/中文名称（Intelligence、Expression、Dexterity、???、???、???）
- 6 种材料/仓库类型（Wood、Papyrus、Oil、Stone、Copper、Gold/Jade？）
- 事件牌、收入芯片、目标芯片、属性芯片的完整集合与效果
- 24 个地点板块的完整数据
- 22 个模组等级二/等级三的具体效果
- 单人模式（V.I.C.I）是否纳入本项目范围

## 相关记忆
- [[civolution-progress]] — 第二款游戏的整体进度与阶段计划
- [[ontology-design]] — 本体扩展约定
- [[project-overview]] — 项目阶段与当前重点

**Why:** 记录从口播稿、卡牌图片和英文规则书中学到的规则，避免每次重新开始。
**How to apply:** 继续以「规则 → ontology 扩展 → piece 表结构 → 单张卡牌录入」的顺序推进；下一步可从词汇表/图例补齐 6 特征名称与 5 种材料名称，或继续扫描更多卡牌验证 piece 表结构。
