# 讲规动画：进度与待办（2026-09-21 收工）

> **本文件已按 v2 全量迁移更新；下方“最新进展/追加”是按时间累积的历史记录。**
> 结论冲突时，以本块和 `tutorial-animation-state.md` 顶部的【最终】为准。

## ✅ 最终状态（2026-09-21）

- **v2 源唯一权威**：`games/splendor/tutorial/anim/v2/full.anim.json`
  - 109 cues / 8 trees/stages；`script.story/note/tree/transition/camera/enter/exit` + `events`。
- **v2 编译产物**：`games/splendor/tutorial/anim/v2/full.compiled.json`
  - 运行时只读 compiled；Unity 默认 `TutorialCuePlayer.track = "full"`。
  - compiled 是生成物但入库；改源后必须重跑 `compile_animation_v2.py` 并过 `--check`。
- **旧 v1 已删除**：
  - `anim/full.json`、`anim/_stage/*.json`、`ui01_30.*`、`games/splendor/tutorial.json`。
  - `TutorialCueAnimPlayer`、`ZoneStore`、`TutorialCueAnimData`、`CueAnimActor`、`TutorialDirector`、
    `TeachingPlayer/Data/Assets`、`TweenLibrary`、`TutorialPrimitives`、`GameSpriteFactory`、
    旧 `TutorialFrameCapture`。
  - 旧 v1 静态/采样工具：`validate_cue_anim.py`、`check_cue_script.py`、`check_framing_flow.py`、
    `framing_geometry.py`、`dump_states.sh`、`flow_to_tutorial.py`、`validate_tutorial.py` 等。
- **v2 工具链**：
  - 静态 schema / 契约：`anim_schema_v2.py`、`check_anim_v2.py`
    - `check_anim_v2` 2026-09 追加 **stage 布局重叠检查**：用同一套 `slot_at` 几何 + 编译态实际件数，
      同一状态下两个 zone 的占用矩形相交就报 warning。cue25 的贵族市场因此从 z=2.34 上移到 2.55。
  - 编译 / 几何：`compile_animation_v2.py`、`anim_geometry_v2.py`
  - 规则过账：`validate_anim_rules_v2.py`（复用共享规则内核）
  - Unity 采样 / 对账：`TutorialV2Sampler` + `dump_anim_v2.sh` + `check_anim_v2_sample.py`
  - C# 编译：`check_unity_scripts.py`
- **最终验收结果**：
  - schema：109 cues，0 warnings
  - compile `--check`：通过
  - 契约 vs 编译快照：109/109 通过
  - 规则过账：109/109 通过
  - Unity 批处理采样：109 cues，对账 109/109 通过
  - C#：18 个文件编译通过
- **后端 key 问题已解决**：
  - Linux `.env` 的旧 `DEEPSEEK_API_KEY` 是废弃 key（401）；
  - Windows 侧全局新 key 可用；已把 Linux `.env` 替换为新 key（`.env` 被 gitignore）。
  - 现有 16 条手写动画合法性问句 16/16 通过；服务地址 `http://localhost:5000`。
- **未做**：用户主动跳过的 Unity 视觉验收。

> 新会话请**先读本文件**，再看 `tutorial-animation-state.md`（大本营：设计、口径、踩坑史）。

## 〇、最新进展（2026-09-21，本会话）

### P0 第 1 步：多棵树骨架 + 盒面树 / 宝石演示树已落地

- 引擎（C#）：`TrackAnimDoc.trees` + `CueAnimDoc.tree` 已接入；`LoadCue` 按 cue 的 tree 取 stage，
  **跨树 = cut**：重新 load stage、从该树根重放入口链、重置取景，不接续上一棵树状态。
- 新增两棵树：
  - `_stage/splendor.box.json`（盒面树）：无 zone / 无 template，只有 `default_picture=media/box.png`；
    `bg.intro.*` 共 8 条已迁入。
  - `_stage/splendor.gems.json`（宝石演示树）：只含 `gem_display`、`gold_display` 和 `gem_sample`；
    `setup.gems.001.1` / `.2` / `.3` / `setup.gems.002` 共 4 条已迁入。
- 主树 `_stage/splendor.table.json`：删掉 `gem_display`、`gold_display` 两个假 zone 与 `gem_sample` 模板；
  `setup.gems.003.1/003.2/005.2`、`setup.nobles.001.1` 的契约/事件已去掉对应引用。
- `full.json` 顶层新增 `trees`（box / gems_demo / main，含 name/why/initial/extent_note 文字说明）。
- Python 工具：`validate_cue_anim`、`validate_anim_rules`、`check_cue_script`、`check_framing_flow` 已按树分治；
  跨树不再默认继承父 cue，取景/状态链都按树各自成立。

### 本会话验收结果

```text
validate_cue_anim.py          109 条，0 错 0 警
validate_anim_rules.py        109 条合法性通过
check_unity_scripts.py        25 个 C# 文件编译通过
dump_states.sh + check_cue_script.py --all   0 处不一致，11 条取景警告
check_framing_flow.py         1 处（action.nobles.forced.001.1 特写→整桌→回同一特写）
```

### 2026-09-21 追加：卡片演示也全部切出主树

用户明确：“卡片演示可以切走。所有的演示都可以有自己单独的树。”已继续落地：

- `setup.cards.001.*` → `cards_intro` 独立世界（stage `_stage/splendor.cards_intro.json`）。
  根画面仍是盒面，第一条 cue 关掉；样卡/卡背模板只存在这棵树里。
- `action.cards.intro.001` … `action.cards.limit.001.2`（20 条）→ `cards_demo` **overlay 树**
  （stage `_stage/splendor.cards_demo.json`）。
- 主树 `_stage/splendor.table.json` 删掉 `showcase*`/`sample_*`；所有不再用样卡的主世界 cue 契约里的
  空 `showcase` 引用已删除。`action.cards.summary.*` 仍在主树（它讲的是真实发展区）。
- 引入 `StageTree.world` 状态世界口径：
  - `box` / `cards_intro` / `gems_demo` 各自独立 world，进入时 Reset + 从树根重放；
  - `main` 与 `cards_demo` 的 `world` 都是 `real`，换 stage 做视觉 cut，但**共享同一份 Store 状态**。
  这样 `action.cards` 里的买牌/补市场会继续影响真实主树，后续 nobles/结算契约不用重写。
- 换树时的镜头：`LoadCue` 现在会在返回前先应用本条 `at=0` 的事件（尤其 `wait camera`），
  stage 与镜头在同一帧切好；不再出现“树先切、镜头等 Update 才切”的交界脏帧。
- cue15（`setup.gems.001.3`）原来带着旧的显式 `camera_padding:1.25`，所以只有它改镜头。
  现已**彻底删除该 cue 的 camera 事件**，自动继承 cue14 的 `gem_display` 机位；
  实测 `dumpCues setup.gems.001.2,setup.gems.001.3` 两条 `orthoSize` 都是 1.50。
- `check_framing_chain` 不再把“本条没写 camera = 自动继承”误报成脏帧；真正要硬保证的是树入口 at=0 camera。
- ✅ 单 zone 特写 `camera_fill` 已修：引擎单 zone 分支现在也按 `1/Clamp(fill,0.2,1)` 取景，
  Python framing 镜像与取景检查同步。
- ✅ gem 演示段镜头统一（按 **UI cue 号**）：
  - UI cue14=`setup.gems.001.1`：显式 `gem_display` + `camera_fill:0.72`
  - UI cue15=`setup.gems.001.2`：删除自己的 camera，自动继承
  - UI cue16=`setup.gems.001.3`：无 camera，继续继承
  - UI cue17=`setup.gems.002`：无 camera，继续继承；为让黄金样本仍在近景里，`gold_display` 已从 x=0.90 挪到 x=0.25
  实测这 4 条 `orthoSize` 都是 **0.95**，从 UI cue14 到 cue17 完全不切镜头。
- 这条已升级为**硬规则**：每条“换树/起树”的第一条 cue 必须在 `at=0` 显式声明 `camera`
  （哪怕只是 `camera:"board"`）。`validate_cue_anim.py` 新增“换树镜头口径”检查，
  缺了直接 error；不是靠我记得手改。
- 引擎 `ReplayEntryChain` 已按 **world** 重放：跳进 `cards_demo` 时会先重放主树前序事件，再切到
  cards_demo stage；跳回主树时状态连续。已实测：
  `action.cards.market.001.2` 跳转入口 `market=12 deck1=36`、
  `action.nobles.intro.001` 跳转入口 `market=12 deck1=35`，两条单 cue 对账均 PASS。

### 追加（用户 2026-09-21）：供应堆数量演示去掉高亮

- UI cue18=`setup.gems.003.1`、cue19=`setup.gems.003.2`、cue20=`setup.gems.004`
  的 15 条 `highlight` 全部删除；只保留 camera + create（4/5/7 枚的逐步出现）。
  用户口径：这种“把每堆都点亮一下”没有意义。

### 追加（用户 2026-09-21）：UI cue23/24 贵族演示独立成树

- `nobles_demo` 现在是**独立世界**（`world=nobles_demo`），舞台 `_stage/splendor.nobles.json`
  只包含 `noble_market` + `noble` 模板，场上天然只有贵族，没有任何主桌状态。
- UI cue23=`setup.nobles.001.1`：`noble_market` 特写 + 一次 `create` 3 块贵族。
- UI cue24=`setup.nobles.001.2`：同树、同机位，贵族已在，不再重复 create。
- 宝石 7→4 的收尾改到 UI cue22=`setup.gems.005.2`（口播“其余的宝石放回盒子”那句）执行。
- UI cue25=`setup.nobles.002` 回主树时，在主世界 `create` 3 块贵族，后续 `action.nobles.*` 才有贵族可用。
- 实测：cue23/24 场景里只有 **3 件**（3 块贵族），`orthoSize=0.77`；对账 0 处不一致，9 条取景警告。

**为什么原对账没抓到？** 这正好暴露了检查口径的边界：
  - `check_cue_script` 比的是“手写契约 vs 引擎采样”：旧 cue23 的契约和事件**一起写错**（都写 supply + destroy 宝石），
    所以两边一致 → 对账 PASS；它只能证明“数据 == 引擎”，不能证明“数据 == 口播意图”。
  - `validate_anim_rules` 只判断规则合法性：7→4 的 destroy 合法、贵族 create 也合法，所以规则也 PASS。
  - 取景检查只查“切镜头脏帧 / 同主体来回跳”，不查“这条口播讲贵族，但结构里没有贵族”。
  - 需要补的正是第六节记的「文字与结构一致性 lint」：从 `story/note` 抽“这条在讲什么”，
    再要求对应 zone/template/事件真的出现。

### 追加（用户 2026-09-21）：组件介绍树审计 + 起始玩家标记补独立树

按“组件介绍应该天然只有该组件”逐段检查：
  - 盒面介绍 = `box` 独立世界 ✓
  - 发展卡介绍 = `cards_intro` 独立世界 ✓
  - 宝石/黄金介绍 = `gems_demo` 独立世界 ✓
  - 贵族介绍 = `nobles_demo` 独立世界 ✓
  - **起始玩家标记介绍（UI28）之前仍在主树** ❌

已补：
  - 新树 `marker_demo`（独立 world） + 舞台 `_stage/splendor.marker.json`，场上只有 `player_marker` 和 1 枚标记。
  - UI28=`setup.starting_player.001.3`：只 create `starting_marker`，不再和主桌混在同一 world。
  - UI29=`setup.end.001.1` 回主树时，在主世界补 `create` 标记，后续设置完成检查仍有 `player_marker=1`。
  - 舞台补了 `glow_zone` 模板，UI28 的区域高亮不再报“stage 缺少 glow_zone”。
  - 实测 UI28 场景只有 1 件（标记），对账 0 处不一致。

### 追加（用户 2026-09-21）：宝石数量演示独立成树，主桌固定 2 人局 4 枚

- UI18=`setup.gems.003.1`：主树按 2 人局 `create` 每色 **4 枚**，这是实际设置动作。
- UI19=`setup.gems.003.2`：3 人局 5 枚，放在**独立演示树** `gems_count_demo`，只显示五个供应堆（25 件），
  **不碰主桌**。
- UI20=`setup.gems.004`：4 人局 7 枚，同在 `gems_count_demo`（35 件），仍不影响主桌。
- UI21=`setup.gems.005.1`：切回主树，确认实际供应堆仍是每色 4 枚。
- UI22=`setup.gems.005.2`：主树只 `create` 5 枚黄金；宝石仍 4 枚，**不再做 7→4 的 destroy**。
  口播“其余的宝石放回盒子”在 2 人局实际流程里没有多余动作。
- `validate_anim_rules.py` 的 `FROZEN_FROM` 保持在 `setup.gems.003.1`：主世界 4 枚建立后锁住；
  `gems_count_demo` 是另一个 world，5/7 枚不会污染主世界。
- 实测：UI19 场景 25 件（5×5），UI20 场景 35 件（7×5），主树 UI18/21/22 始终 4 枚/色；
  对账 0 处不一致，4 条取景警告。

### 本会话验收结果（更新版）

```text
validate_cue_anim.py          109 条，0 错 0 警
validate_anim_rules.py        109 条合法性通过
check_unity_scripts.py        25 个 C# 文件编译通过
dump_states.sh + check_cue_script.py --all   0 处不一致，11 条取景警告
单 cue 跳转实测                action.cards.market.001.2 / action.nobles.intro.001 均 PASS
check_framing_flow.py         1 处（action.nobles.forced.001.1 特写→整桌→回同一特写）
```

### 下一小步

- 给剩余仍走默认主树的 cue 显式补 `tree: "main"`，并补全 `trees` 文字（P0 第 2 步）。
- 可以开始按「每棵树的 extent 特写」做视觉验收：卡片树、宝石树、主树各自的镜头是否已不受主桌纵深影响。

## 一、当前状态（全绿）

- **109 条 cue 全部有动画数据**（`games/splendor/tutorial/anim/full.json`，手写资产）。
- 检查（都可直接跑）：
  - `python3 scripts/validate_cue_anim.py` → **0 错 0 警**
  - `python3 scripts/validate_anim_rules.py` → **过账**：109 条逐步合法（每色在场 4、黄金 5、手上限 10、
    保留上限 3、买牌「价格−折扣==实付」；15 张卡价格已在 `games/splendor/card_facts.json`）
  - `./scripts/dump_states.sh` + `python3 scripts/check_cue_script.py --all` → **对账 0 处不一致**，
    另有 **11 条取景警告**（本轮后；画面里有契约没提到的组件，等用户裁决）
  - `python3 scripts/check_unity_scripts.py` → 25 个 C# 文件编译通过
  - `python3 scripts/check_framing_flow.py` → 取景"同主体来回跳"检查，**剩 1 处**
- 最近提交：`75885aa`（P0 第 1 步：盒面树 + 宝石演示树落地，引擎按 tree 切 stage）；Windows 已同步。
- 问答引擎：`backend/BoardAI.Api`（新 key 走环境变量 `DEEPSEEK_API_KEY` ✓；仓库里 `LLM:ApiKey` 已清空 ✓）。
  日志与手写问题在 `games/splendor/tutorial/anim/_qa/`（**16/16 通过**）。

## 二、今天定下的工作方式（重要，别再走回头路）

1. **动画脚本 = 手写的静态资产**（`full.json`）：story/契约/事件/取景全由人写。**不要生成器** ✗；
   程序只做"确定无疑"的事（体检 / 过账 / 编译 / 对账 / 取景流检查 / 问句的执行）。
   旧的 `batch*.py` 与 `anim_framing.py` 已退役（`.claude/anim_batches/`，**别再运行**：会整份重写 full.json）。
2. **合法性问句手写**（`_qa/questions.json` + `run scripts/qa_anim_ask.py`）：一 cue 一事、只带最小状态、
   不用教程自造词（"样本"）、规则自动发生的事就说成自动 ✓。（机器拼的版本 21 问 / 7 可疑 / 20~125s ✗；
   手写版 16 问 / 0 可疑 / 2~9s ✓）
3. **取景**：`camera` 写"要入镜的 zone"（逗号分隔），`camera_fill` = 这几个 zone 占画面中央的比例
   （默认 0.8 = 老观感；特写 0.6~0.72）。镜头**默认继承父链**（同一 cue 不管怎么跳画面完全一样 ✓）。
4. **用户验收节奏**：我写 → 用户看 → 我改 → **改完即成固定资产**（只用于播放）。

## 三、待办（按优先级）

### P0 —— 多棵树迁移（下一个主工程，用户已明确要）

设计见 `tutorial-animation-state.md` 的「★ 多棵树」一节。分四步，**每步都要能单独验收**：

1. **先搬最简单的两棵**：盒面树、宝石演示树（样本件搬进各自树；主树里删掉对应的
   create/destroy 与 `showcase`/`gem_display` 假 zone）。目的：验证"每棵树自带 extent →
   特写不再被主桌 10.6 的纵深顶住" ✓
   - ✅ 2026-09-21：盒面树 + 宝石演示树已落地；`gem_display`/`gold_display`/`gem_sample` 已从主树删除。
   - ⏳ `showcase` 卡片演示区仍在主树；下一步先搬 `setup.cards.001.*` 介绍牌树，再单独设计 `action.cards.*` 的样卡树。
2. **数据**：给 109 条 cue 标 `tree`；`full.json` 顶层加 `trees: [{id, stage, initial}]`；
   契约链**树内**成立；跨树 cue 声明 tree + 该树入口状态（= cut ✓）
3. **引擎**：`LoadCue` 按 cue 的 tree 取 stage；**换树 = 换 stage + 重放该树入口链 + 原子换画面** ✓；
   树内行为不变（不重建 ✓）。顺带根治"引擎读不到 `entry_from`"（每棵树自己的父链 ✓）
4. **检查按树分治**：采样器按 cue 跳进对应树；对账 / 契约链 / 取景链分别按树校验；
   把"同一条 cue 的画面由 cue id 唯一决定"变成**可验证的不变量** ✓

### P1 —— 取景（第二批手写，等第一批验收手感）

- 第一批**已完成**：`setup.cards.001.1`、`action.cards.intro.001`、`setup.gems.001.2`、
  `setup.starting_player.001.3`（fill 0.72）、`setup.cards.002.1/002.2`（三摞牌库+市场 0.72 ✓ 用户说"比例很完美"）
- 第二批候选（横向宽、纵向窄，闸门已不再误伤 ✓）：**买牌进发展区**、**结算（发展区+贵族）**、
  **拿三色宝石**、**市场一格** —— 每条要顺手把"入镜的 zone"**手写进契约**（现在靠人写，不自动补 ✓）
- 顺手收掉：`action.nobles.forced.001.1` 的**跳切**（贵族特写 → 整桌 → 又回贵族特写 ✗）

### P2 —— 遗留问题（等用户裁决/配合）

- **12 条取景警告**：供应堆特写里能看见市场/牌堆 —— 收窄取景还是把邻区写进契约？（等裁决）
- **两处原语缺口**：①"错误示范 + 叉掉"需要**撤销/临时状态**层；②第 4 节 4 人局例子只有 A/B 玩家区
- ~~**贵族三块长得一样**（都显示 贵族_0001）~~ 已修（2026-09-21）：stage 增加 noble_1..noble_5，模板分别接 贵族_000X_cutout.png；cue23 t=0 出现 noble_1/2/3。
- **起始玩家标记尺寸**是我估的 50×62mm，等用户实测
- **整桌镜头偏小**：主桌 extent 高（min_z -5.60 / max_z 5.00）—— 多棵树落地后，主树范围可另行收紧
- `entry_from` 严格解析（让引擎能读运行时轨道）—— 多棵树那步顺带解决 ✓

## 四、常用命令

```bash
python3 scripts/validate_cue_anim.py            # 体检（数据/本体/取景链/字段归属层）
python3 scripts/validate_anim_rules.py          # 过账（重放状态逐步验规则）
./scripts/dump_states.sh && python3 scripts/check_cue_script.py --all   # 对账（要 Unity，Windows）
python3 scripts/check_unity_scripts.py          # C# 编译（带 stub）
python3 scripts/check_framing_flow.py           # 取景"同主体来回跳"
python3 scripts/qa_anim_ask.py [--list|--only cue]   # 手写合法性问句问引擎（需后端在跑）
./scripts/sync_workspaces.sh from-linux|from-windows|status
```
Unity：`/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe -batchmode -projectPath 'D:\workspace\board\client' ...`
（`G` 开动画、`B` 轮换关键帧、空格暂停、`←/→` 逐步、`A` 自动播）

## 五、明天的第一件事（建议）

问用户"第一批特写验收结果"（`setup.cards.002.1/002.2` 的机位、以及两份走法是否一致 ✓），
然后**按 P0 第 1 步**开多棵树迁移（先搬盒面树 + 宝石演示树）。

## 六、追加原则：脚本要写成「**文字版动画**」，树的维护也要写进去（用户 2026-09-21）

用户原话："这个**树该怎么维护，也要在脚本中体现**。换句话说，脚本要写成'**文字版动画**'，
而不是只简单说下画面该怎么动。**文字类型的脚本负责的工作越多，我相信最终成品质量就越稳定**，
因为 LLM 比起图片明显更擅长文字处理。"

落地要求（多棵树那步一并做）：
1. `trees` 块不只是 id 列表，每个树要**用文字写清**：叫什么、**为什么要它**（它在讲什么）、
   初始状态是什么（该有什么、不该有什么）、extent 大概什么样；
2. 每条 cue 要写清**它在哪棵树、以及树是怎么被维护的**：这一条是"在树内推进"还是"**切树**"？
   切到哪棵、为什么此刻切（讲完演示件回到主树之类）——这是 cut 的语义 ✓，必须写在数据里，
   不能只靠 zone 列表暗示 ✗；
3. 一条 cue 的"文字"至少包含：**念什么（story）** + **画面要变成什么（enter/exit）** +
   **这一刻在讲哪件事、为什么这么演（note）** + **在哪棵树/怎么切树（tree）** + **看哪几个 zone（camera）**；
4. 由此得到的**稳定性来源**：整部动画可以先当**一篇文章**读懂 → 由文字发现矛盾（比看画面快得多）✓；
   机器可查的部分（契约/事件/取景/tree）全部可对账 ✓；人（或 LLM）只审"文字说得对不对" ✓。

**顺带的检查想法（先记下，别急着做）**：做一个"文字与结构一致性"lint ——
比如 `note`/`story` 里提到的 zone/组件，若不在契约或取景里，就提醒一句（防"文字说了、结构没做" ✗，
以及"结构做了、文字没提" ✗ 这两种脱节）。这正好是"文字负责更多"的落地方式 ✓。
