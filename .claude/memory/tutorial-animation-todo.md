# 讲规动画：进度与待办（2026-09-21 收尾，明天继续）

> 新会话请**先读本文件**，再看 `tutorial-animation-state.md`（大本营：设计、口径、踩坑史）。

## 一、当前状态（全绿）

- **109 条 cue 全部有动画数据**（`games/splendor/tutorial/anim/full.json`，手写资产）。
- 检查（都可直接跑）：
  - `python3 scripts/validate_cue_anim.py` → **0 错 0 警**
  - `python3 scripts/validate_anim_rules.py` → **过账**：109 条逐步合法（每色在场 4、黄金 5、手上限 10、
    保留上限 3、买牌「价格−折扣==实付」；15 张卡价格已在 `games/splendor/card_facts.json`）
  - `./scripts/dump_states.sh` + `python3 scripts/check_cue_script.py --all` → **对账 0 处不一致**，
    另有 **12 条取景警告**（画面里有契约没提到的组件，等用户裁决）
  - `python3 scripts/check_unity_scripts.py` → 25 个 C# 文件编译通过
  - `python3 scripts/check_framing_flow.py` → 取景"同主体来回跳"检查，**剩 1 处**
- 最近提交：`bca5341`（多棵树设计入档）；Windows 已同步。
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
- **贵族三块长得一样**（都显示 贵族_0001）—— 五张贵族扫描件还没分别接进舞台
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
