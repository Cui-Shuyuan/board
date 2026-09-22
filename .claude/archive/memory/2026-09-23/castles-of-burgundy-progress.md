---
name: castles-of-burgundy-progress
description: 勃艮第城堡（第五款游戏）规则定义进度——概念层 73 objects/5 actions/12 triggers/15 conditions + 流程层首版完成（校验全绿、索引已建），QA 基线 52 题进行中
metadata:
  type: project
---

# 勃艮第城堡（The Castles of Burgundy）规则定义进度

## 文件位置

- `games/castles-of-burgundy/concepts.json` — 概念层：objects 73 + actions 5 + triggers 12 + conditions 15，校验全绿
- `games/castles-of-burgundy/flow.json` — 流程层：Setup → 5 阶段循环（阶段准备 → 5 轮循环 → 阶段结束收入）→ 终局（25 轮，回合顺序按石桥）
- `doc/castles-of-burgundy/Castles_of_Burgundy_Rules_EN.md` + `doc/castles-of-burgundy/faq.md` — 规则书与 FAQ（52 题 QA 数据源）
- QA 脚本：`scripts/_qa_cob_run.py`（FAQ 40 题解析 + 12 题合并节手工整理）、`scripts/_qa_cob_log_analysis.py`（A-E 来源分析）

## 范围与抽象（首版锁定决策）

1. **仅基础规则**（无扩展/单人/团队）；参考璀璨宝石+文明演化写法（波多黎各/伯明翰未 review 不参考）
2. **26 个修道院黄色瓷砖全部独立概念化**（monastery_tile_1..15、8 个建筑计分、24/25/26），编号仅 #17 瞭望塔、#22 银行被规则书锚定
3. 阶段准备用**互补条件双分支**（A 阶段 vs B-E 阶段）：步骤级条件阻断会停 do_after 链，所以清板块+补货不能串成一条链
4. 阶段奖励（矿收入/修道院工人收入）用 key-as-type 触发器调用（splendor `<refill_market>` 先例）

## 关键规则口径（写 flow 时踩过的点）

- **回合顺序按石桥**（离市中心近者先，叠堆最上先），与座位无关——初始由掷骰决定
- 每轮三步：①全员同时掷 2 骰（起始玩家加掷白骰）②白骰放货（round_space 最上面 1 块到对应 depot）③石桥顺序各行动一回合（2 骰行动 + 至多 1 次黑仓购买）
- **阶段开始清板块**：A 阶段跳过（空版图直接补）；货物从不被清除；清走的板块永久移出游戏（不回板块堆）
- 终局 tiebreak 三级：总 VP → 庄园空位少 → 石桥更靠后；2026-08-16 判胜改为 FILTER（三道筛子，`<most_vp_wins>` 复合条件保留）
- FAQ #2「船/矿/城堡可留下因相同」是玩家观察非规则——按规则书全部清走
- FAQ #18 船取货超限：规则书要求能存下（3 色上限），FAQ 承认规则倾向全部取后处理——按规则书

## 待核实（对着实物）

- 第 4 种动物（牛/羊/猪/鸡——鸡待核实）；货物 6 色（turquoise/pink/brown 来自 MD，red/purple/orange 来自 FAQ 推断）；货物骰点映射表；石桥长度（~40）；黄色瓷砖 #15-26 与 8 建筑计分的精确编号

## QA 基线（2026-08-16，scripts/_qa_cob_results.jsonl + _qa_cob_retest.jsonl，52 题）

- **52/52 答对**（首轮 46 对 6 错 → 6 处数据/检索强化后复测全对）；来源分析：**A 41 / C 11 / D 0 / E 0**
- 首轮 6 错及修复：Q2/Q3 阶段清板口径（depot 描述补「移出游戏不回堆」）、Q4 存储位与货物存储混淆（key_space 描述加口语「存储位」+ 与 goods_storage 辨析）、Q5 每次拿取弃板粒度（take_hex_tile 描述补「每次拿取独立判定、一回合最多弃两块」）、Q39 3 人局格位（depot/game_board 补「标 2 和 3 的空位」+ 6 号仓库深绿格 A/C/E 城堡、B/D 矿特例）、Q42 黄色瓷砖口语查询检索失败（26 块修道院瓷砖全部加「黄色瓷砖 #N」别名）
- 检索机制发现：identify 口语查询（「黄色瓷砖 #N」）常空手而归，LLM 靠候选里猜到概念 id 后 `explain <id>` 兜底；混合评分（向量+关键词）下含数字的描述会被关键词通道虚高（如「endgame」因「5 轮」得 0.9）——数据侧加口语别名可让正确概念压过噪音

## 下一阶段

1. 用户整体 review（FAQ 与规则书分歧点按「MD 为准」处理，用户已说过 FAQ 的 A 不一定对）
2. 待核实清单（对着实物）：第 4 种动物、货物 6 色与骰点映射、石桥长度、黄色瓷砖 #15-26 与 8 建筑计分的精确编号、3 人局每仓库精确格数
3. 服务三步走（同其他游戏）：改规则 → 重建索引 → **重启 API**（LoadJson 缓存永不失效）

**Why:** 第五款游戏的进度与 QA 基线，用户 review 和后续迭代的背景材料。
**How to apply:** review 时对照「关键规则口径」与待核实清单；改规则后记得三步走；阶段准备的双分支互补条件模式可复用于后续有「首个阶段例外」的游戏。
