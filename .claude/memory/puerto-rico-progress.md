---
name: puerto-rico-progress
description: 波多黎各 1897（第三款游戏）规则进度与 QA 状态——首版完成、问答三层体系验证、用户将于 2026-08-17 整体 review
metadata:
  type: project
---

# 波多黎各 1897（Puerto Rico 1897）规则定义进度

## 背景

用户 2026-08-23 前后有波多黎各实局。规则书：`doc/Puerto_Rico_1897_Rulebook.md`（Ravensburger 2022 年版「波多黎各 1897」，不是经典 2002 版；doc/ 已 gitignore）。范围：基础版 3–5 人，不含双人规则与扩展 I–IV。

## 文件位置

- `games/puerto-rico/concepts.json` — 概念层（~70 对象 + 8 角色行动 + 21 条件，校验全绿）
- `games/puerto-rico/flow.json` — 流程层（Setup 18 步 → 年度循环 → 终局计分；2026-08-16 判胜改 FILTER：总分最高 → 钱币+货物更多，`<most_vp_wins>` 复合条件保留）
- `doc/faq/puerto rico/faq.md` — Grok 抓的 55 道 BGG 真实提问（**基于经典版，术语与 1897 有差异**）

## 工位概念抽象（2026-08 用户定稿）

- **`<work_slot>`（工位）已抽为 PR 游戏层概念**，只放 `games/puerto-rico/concepts.json`，不进 ontology。
- 所有板块（庄园、采石场、生产建筑、商业建筑、大型商业建筑）都通过 `"<work_slot>": n` 直接声明工位数；基类 `estate_tile`/`building` 的 `constraints.required` 中直接写字符串 `"<work_slot>"`。
- 原 `semicircles` 字段已删除；描述中的「半圆槽」统一改为 `<work_slot>`，「半圆形工位」保留为外观描述。
- constraints 概念引用约定：无需语境说明时写纯字符串（如 `"<work_slot>"`）；需要语境说明时在 required 内写 `{ "<good>": { "description": ... } }`（概念作 key，值对象是增强描述），不另在外层重复声明。
- 校验 E20：concepts.json 中普通字段名不得与已定义概念同名（本文件或 ontology，如 `<good>`/`<cost>`）；已同步修正 agricola `cost`、civolution `component`。
- PR 建筑已删除 `board_section` 枚举，只保留 `max_quarry_discount` 表示“最多能被采石场折扣减免几块钱”。
- `large_commercial_building` 基类直接写具体值：`city_spaces: 2`、`price: 10`、`vp: 4`、`copies: 1`、`max_quarry_discount: 4`，不再用 schema 包装。
- PR 建筑费用建模：`building` 只声明 `"price": N` 作为接口；`buy_building`（`<ontology::play>`）里定义 `<ontology::cost>` + `<ontology::instant_cost>` + `<ontology::transfer>`，quantity 只引用 `this.<ontology::piece>.price`；采石场折扣/选角者特权依赖当前状态，目前放在 description 说明，不做伪公式。
- PR 中直接以 `<ontology::continuous_content>` 作 part 的光环效果（`quarry_tile`/`builders_yard`/`office`）已改为方案 A：外层用 `<ontology::continuous_effect>` 包 condition + content。
- 生产职责定稿：由 `craftsman_role` 承担流程与聚合计算；`estate_tile` 与 `production_building` 只声明 `<good>` + `<work_slot>`，不持有 effect。基类 required 用 `{ "<good>": { "description": ... } }` 增强语境，具体板块直接写 `"<good>": "<fruit>"`。
- 商业建筑与大建筑暂按 `count: 1` 填写；若实物确认有不同工位数，只改对应 count。

## 1897 版关键机制（与经典版差异，写规则时踩过的点）

1. **工作登记册**按「所有玩家建筑板块的空<work_slot>数、至少玩家数」补充；**工人不够补充 = 终局条件**
2. **庄园公开陈列**（暗堆翻出玩家数+1 块任选）；暗堆空洗弃牌堆
3. **采石场折扣按建筑区块封顶**：顶层（费 1–2）减 1、第二层（3–5）减 2、第三层（6–9）减 3、大建筑减 4
4. **工人可移动**（招募阶段）；**大建筑未占据也计 4 VP**，加成才需占据
5. **3 人局没有冒险家**；货船按人数取 4-6/5-7/6-8 三艘
6. 船长阶段唯一强制行动；码头包租船可在装运阶段**任意时刻**用（含装普通船前），轮到自己装货时普通船/码头**二选一**
7. 终局三条件（工人不够补登记册/城区 12 格满/最后一枚 VP 筹码被拿）——**回合打完才结束**

## 待实局核实（规则书图片无法提取，请对着实物确认）

1. **生产建筑份数**：规则书只给总数 20，按 2/2/4/4/4/4 写入
2. **商业建筑与大建筑的工位数**：正文未提（影响登记册补充计数）
3. **交易所售价**：玉米=0 有例证，其余按经典版 1/2/3/4

## 2026-08-16 会话：QA 实测与系统级修复（提交 7f8d37c → 7857951）

### 规则层修复（QA 暴露）

- 码头 wharf_shipment 的 do_after loading_rounds 与「任意时刻」矛盾——去掉 do_after，明确「普通船/码头二选一」
- 种植 `_skip` 描述暗示「能种必须种」——改为明确可选；工匠生产步骤补 `_skip` 候选
- choose_role / role_card / recruiter_role 显式化「除船长外角色行动均可选」
- `<wharf>` 建筑本体描述与 captain_role 表述对齐（「不能用包租船逃避」旧文案清除）

### 问答三层结构（用户定稿方向，代码已实现）

- **tier1** 精确命中直接返回事实；**tier2** 未精确命中时程序自己跑语义检索补候选（unresolved + Candidates 含相似度分，确定性名称包含候选无条件保留）；**tier3** 语义也无可信候选 → `Status=no_match` + 显式消息（先建议 list/identify 恢复，再允许自行发挥）
- 回答按会话证据打 tier 标记写入日志：`tier1-数据回答 / tier2-候选兜底 / tier3-无数据自行发挥`
- 语义候选阈值 **0.50**（bge-base 实测：噪声 0.36–0.47、可靠 ≥0.53）
- 详见 [[runtime-architecture]] 与 [[vector-search]]

### 嵌入模型升级 bge-small → bge-base-zh-v1.5（用户不满区分度，实测换型）

- 小模型窄锥噪声 0.80–0.84 vs 信号 0.85–0.89（间距 0.05≈随机）；大模型 0.36–0.47 vs 0.53–0.73（间距 0.2，阈值 0.50 干净切分）
- 转述查询 top1：base 全对（领工人的角色→工人、卖货换钱的地方→交易所）；经典别名（杜布隆/市长/殖民者/探矿者）**两模型都失败**，base 下掉入噪声带被干净拒掉
- 改动：EmbeddingService/rebuild_index.py 条件加 token_type_ids；appsettings.ModelDir 指向 bge-base-zh-v1.5；三款游戏索引重建 768 维；旧模型目录保留可回滚
- 基准脚本 `scripts/_bench_embedding_models.py`

### FAQ 三轮基线（scripts/_qa_pr_results_*.jsonl，口径 A=检索答对/B=兜底/C=凭记忆/D=凭记忆错/E=有数据错）

| 配置 | A | B | C | D | E |
|---|---|---|---|---|---|
| small+旧代码 | 51 | 0 | 0 | 0 | 4 |
| base+阈值0.85 | 40 | 1 | 10 | 0 | 4 |
| base+阈值0.50 | 37 | 0 | 14 | 1 | 3 |

**正确率三轮完全持平 51/55**（错题恒为 Q4/Q27/Q38/Q43 问句误解型）。C 上升主因是 **LLM 放弃行为波动**（实测候选质量良好、captain_role 在列，LLM 拿候选后仍凭记忆作答）+ 三轮各改一次消息的干扰。FAQ 层 A/B 需多轮重复取均值才能定论。

### Civolution 103 题复测（本地题集，tier1 率 92%）

tier1=95 / tier2=1 / tier3=7。tier3 全部行为合理（离题拒绝×3、闲聊×1、跨游戏概念反问「贵族」、歧义反问×2）。抽查 5 道 tier1 答案与规则文件对照全部精确一致。tier2 那题（大陆有哪几种地形）实为数据建模空缺——「地形」不是概念而是 territory 属性；**用户决定不改**（客人不会这么问，类比 splendor 问「宝石分几种」）。

### 用户定稿的判断（明天的 review 以此为背景）

1. **经典别名（杜布隆/靛蓝等）不加 aliases**——店里教什么词客人用什么词，FAQ 的经典术语是 Grok 抓 BGG 经典版造成的虚高指标；客人真问「猫眼石」类不存在的东西，tier3 反问就是正确行为
2. **API 文件缓存坑**：`GameRulesService.LoadJson` 按路径缓存 JSON 永不失效——**改规则文件后必须重启 API 才生效**（本轮两次复测被坑）。缓存失效机制待做
3. 记忆不入 git（.claude/memory 已 untrack，见 .gitignore）

## 下一阶段

1. **用户整体 review（2026-08-17）**：FAQ 逐条 review、三处实物参数核实、civolution/波多黎各规则过目
2. 待做清单：manifest.json（波多黎各）、LoadJson 缓存失效、LLM 放弃行为观察（多轮 QA 取均值）、ontology W06/W05 警告清理（用户说过以后修）
3. 服务状态：Qdrant（D:\qdrant 目录启动）+ API 运行中；改规则 → 重建索引 → **重启 API** 三步缺一不可

**Why:** 第三款游戏的进度、QA 基线与会话决策记录，明天 review 的直接背景材料。
**How to apply:** review 时对照「待实局核实」三处实物参数；改规则后记得三步（重建索引+重启 API）；aliases/地形概念均为用户明确不改项，不要翻案。
