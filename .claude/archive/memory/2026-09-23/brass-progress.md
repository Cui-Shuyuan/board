---
name: brass-progress
description: Brass: Birmingham（第四款游戏）规则进度与 QA 基线——概念层+流程层首版完成（提交 f8c8f7f/3b0de9c/be21354），FAQ 50 题 48/50，遗留 FAQ 与 MD 冲突 4 处待用户裁决
metadata:
  type: project
---

# Brass: Birmingham（工业革命：伯明翰）规则定义进度

## 文件位置

- `games/brass-birmingham/concepts.json` — 概念层：objects 46 + triggers 3 + actions 7 + conditions 28，校验全绿
- `games/brass-birmingham/flow.json` — 流程层：Setup → 运河时代 → 铁路时代 → 终局计分（936 行）；两时代共用同一回合定义（运河定义 `round` 内联节点、铁路 `"<ontology::round>": "<round>"` 字符串引用，civolution `<move_tribe>` 先例）
- `ontology/concepts.json` — 新增 `<connected>` 连通状态（extends state，constraints 声明 from/to/via/owned_by，无 params 字段）
- `doc/brass-birmingham/Brass-Birmingham-Rulebook.md` + `doc/faq/brass-birmingham/faq.md` — 规则书与 FAQ（Grok 抓 BGG 真实提问）

## 范围与抽象（首版 4 个锁定决策）

1. **版图抽象化**：location 为概念不枚举（network/connected 按地点判定）；tile 数值（成本/产出/VP）待核实占位
2. **标准 2-4 人规则**（不做 solo/tournament）
3. `<connected>` 进本体（状态层）；`<network>` 留游戏层（本质是玩家状态）
4. 玩家板 tile 数据仅机械层（数值待用户对着实物核实）

## 关键规则口径（写 flow 时踩过的点，QA 验证过）

- **收入每回合都收**，唯一例外是运河时代最终回合（MD line 494）——曾误以为运河时代不收收入
- **商人啤酒只在运河→铁路转换时补**（MD line 531）；FAQ 193 行称「每回合末补」→ **冲突待裁决**
- **铁路时代结束后直接终局计分**：MD「End of Rail Era」一节（line 537-542）重复运河转换步骤，判定为规则书勘误，跳过
- 终局计分：钱 £4=1 分上限 15 → 收入等级 ±分 → **所有 L2+ 板块二次计分（翻面与否都计）**→ most_vp_wins（总分→收入→钱）；2026-08-16 判胜改为 FILTER（三道筛子：总分 → 收入等级 → 剩余钱，`<most_vp_wins>` 复合条件保留）
- 无存在（no presence）= **玩家个人**条件（你无板块/链接即可任意线路建链接、行业卡任意地点建）——Q3 曾误答成全场条件
- 1 级陶器 = 灯泡陶器 = 唯一不能 Develop 的板块（可被建造消耗或 Gloucester 加成移除）——Q19 曾误答方向
- 资源上限：煤 100/铁 90/酒 30；市场空后煤 £8/铁 £6 直购（MD line 314/327）
- 侦察拿到的万能卡**当回合可用**（MD 无限制；FAQ 称「通常下回合」→ FAQ 有误）
- 蓝白等级标记是 Lancashire 概念，MD 伯明翰无此设定（FAQ 该条疑混淆两作）

## QA 基线（2026-08-16，scripts/_qa_brass_results.jsonl，50 题 = FAQ 结构化 29 + 第 8 节手工整理 21）

- **48/50 答对**；来源分析（_qa_brass_log_analysis.py，口径 A=检索答对/B=兜底/C=凭记忆/D=凭记忆错/E=有数据错）：**A 34 / C 14 / D 1 / E 1**
- 两处数据修正后复测全对：`no_tiles_on_board` 补「无存在」同义表述、`pottery_lightbulb_undevelopable` 点明 1 级陶器身份
- 冲突条目：Q38 商人啤酒补充时机——**用户已裁决：以 MD 为准（时代转换时补），FAQ 193 行有误**；其余 Q30/Q44/Q48 同样按「MD 为准」处理（FAQ 该三条为 Grok 抓取的陈旧/混淆条目）

## 下一阶段

1. **用户整体 review**：4 处 FAQ-MD 冲突裁决、tile 数值/卡数待核实（产业卡/地点卡确切数量 MD 组件表缺失）
2. 待核实清单：merchant tile 78 张分面、产业卡各行业数量、野卡 8+11、2/3 人局移除卡精确清单
3. 服务三步走（同其他游戏）：改规则 → 重建索引 → **重启 API**（LoadJson 缓存永不失效）

**Why:** 第四款游戏的进度与 QA 基线，用户 review 和后续迭代的背景材料。
**How to apply:** review 时对照「关键规则口径」与 4 处冲突；改 brass 规则后记得三步走；flow.json 的两时代共用回合引用模式（inline 节点 id 可被跨树字符串引用）可复用给后续游戏。
