---
name: current-state
description: 新会话入口——截至 2026-10-01 的当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

> 最后更新：2026-10-01 · 基线见 git log · 后端 xUnit 75/75

# 当前状态（2026-10-01）

## 一句话

Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` god class 已完成服务化拆分，75 条 xUnit 全绿；当前工程活跃面还包括 Android UaaL 客户端、内容更新 v1、语音问答 v1；动画 full 已进入收尾/暂停状态，Flow Guide 是动画收口后的下一产品方向。

## 当前工作区状态

- 最近提交为动画 world/screen 标注统一（`point`/`shape`/`label` + box/world label）；HEAD 以 `git log` 为准。
- 动画对象原语已统一为 `target` 接口（`entity` / `screen`）；Splendor full 与 schema 示例已迁移，屏幕空间对象也支持 highlight/point/fade/scale。
- 工作区仍有 5 个 Unity 编辑器回写的 Settings/ProjectSettings 文件未提交（与本次动画任务无关）。
- API 接口重构后的 Android APK 已构建成功（`clients/android/app/build/outputs/apk/debug/app-debug.apk`）并安装到测试机；内容已更新到 `2b777171251141ad`，设备 compiled 中 screen modifier clips 与 `entity/screen` 两种 `object_space` 已确认存在。
- 2026-09-23 记录的动画待收口项已经由后续提交收口（time_anchors 见 `38971d4`）。
- `.claude/archive/memory/2026-09-23/` 只用于追溯历史，不作为现状依据。

## 当前已具备的能力

- **后端服务化**：`GameRulesService` 已是薄 facade，只负责构造协作类、public wrapper、`Dispose`、`ClearDerivedCaches`；业务拆为 `RulesContentStore` / `RulesConceptCatalog` / `RulesNameIndexService` / `RulesSearchService` / `RulesIndexService` / `RulesFlowService` / `RulesReferenceService` / `RulesFactService` / `RulesPlanService`。后端 xUnit 测试 75/75 全绿。
- **规则数据与检索**：9 款游戏目录，8 款有 `flow.json`，Splendor 是 Runtime + 动画试点；Qdrant + `bge-base-zh-v1.5` ONNX 检索链可用；`validate_rules.py --errors-only` 为 0 errors / 72 warnings；`tools/qa/retrieval_gold.jsonl` 85 条。
- **动画 tree/状态模型（2026-09-30 修正）**：tree 只决定 stage/可见性，不是状态边界；cue 默认按 `entry -> parent -> 轨道前一条` 继承完整状态，允许跨 tree。当前 stage 缺少的 zone 不渲染，组件仍保留在逻辑状态中，切回对应 tree 后恢复显示。full 轨道现只有 `main` 一棵 tree，其它展示内容改走 screen 对象；该机制仍保留给多树轨道/其它游戏。
- **动画全脚本继承关系（2026-09-30）**：tree 切换默认继承上一条 cue 的完整状态；仅在盒面/卡牌/宝石/贵族/标记等显式 demo 返回点用 `entry` 跳回 canonical 快照。full 现在只有 main，旧展示树切换点已收口。
- **动画对象接口（2026-10-01）**：所有对象原语通过 `target` 接口选择接收者；编译器展开为 flat 字段，Unity 用 `IAnimVisualObject` 同时执行实体和屏幕对象的表现原语。源数据统一写 `show/hide`；编译产物里屏幕对象使用 `overlay_show/overlay_hide` 作为 clip kind。迁移工具 `animation/migrate_object_targets_v2.py`。
- **动画标注层（2026-10-01 收口）**：`point` / 新增 `shape`（`arrow`/`circle`/`cross`/`forbid`/`box`）/ `label` 统一编译成运行时 `FrameState.Annotations`，每条显式带 `annotation_space=world|screen`：world 锚实体并每帧投影（跟镜头/卡牌），screen 锚 overlay/屏幕槽位（不跟镜头）。`label` 新增 world 锚定；`part` 语义锚点和 mapping `offset`/`nudge` 屏幕微调已落地。
- **规则文件 freshness**：`RulesDocumentStore` 按文件 `Length + LastWriteTimeUtc` 自动失效；改规则 JSON 无需重新启动 API 服务；`ClearDerivedCaches()` 会清名称索引、Plan 类型缓存、Flow 位置缓存、Fact score 缓存；语义检索仍需要重建 Qdrant 索引（`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all` 或 `python tools/indexing/rebuild_index.py ...`）。
- **Android 客户端**：已有 Unity as a Library（UaaL）原生 Android 壳、Kotlin + Jetpack Compose 控制层、首页游戏目录/搜索/历史/资源管理、manifest → 本地内容仓库 → 增量下载/断点续传 v1、教程播放器 Compose 控制层与 Unity 状态回传、问答面板、按住说话 PTT、ASR、回答 TTS、自动播放/重播/继续播放。已有 6 个 Android JVM 测试文件（ContentStatusTest、ContentUpdaterTest、HomeContentCoordinatorTest、PlayerSessionControllerTest、QaVoiceControllerTest、UnityLoadQueueTest）；真机结论只保留已有记录部分，完整范围待复测。
- **语音链路**：后端运行时走 Python 短进程桥 `tools/voice/asr_once.py` / `tools/voice/tts_once.py`，对外接口 `POST /api/asr/once`、`POST /api/tts`；默认 TTS provider 是 `standard`（豆包标准语音合成小模型 v1），`--provider seed2` / `DOUBAO_TTS_PROVIDER=seed2` 可切回旧 2.0；standard 路径没有字级 subtitle，旧 seed2 路径有；已存在 Splendor full 音频 manifest 来源为 seed2（`zh_female_vv_uranus_bigtts` / `seed-tts-2.0`）。Android 不直接接触火山凭证，密钥只在仓库根 `.env`（git-ignored）。
- **Catalog / Manifest**：`content/catalog/splendor.json` 已入 Git；`content/manifests/splendor.json` 为生成物、不入 Git（已由 `.gitignore` 排除），当前只有 Splendor 一套。`/api/catalog/games` 是 Android 首页来源，`/api/content/games/{game}/manifest` 和 `/api/content/games/{game}/files/...` 提供 manifest 拉取与内容文件。
- **动画 full**：Splendor full 约 110 cue，`time_anchors` 已全量迁移并 commit（`38971d4`）；cue47 的样卡/桌面可见性问题已按 tree/stage 机制修正（`7ad18e5`）；源数据保留 anchor，compiled 输出数值 `at`。口播 QA 问题可/优先与 cue 同置（`full.anim.json` 的 `qa` 字段），历史问题仍在 `_qa/questions.json`；`qa_anim_ask.py` 可直接提取并自动发送，`compile_tutorial.py --validate-qa` 当前从 `_qa/questions.json` 选受影响 cue 做门禁、`--validate-qa-all` 跑全集。full TTS/runtime/compiled/Unity 链可运行。
- **发展卡身份保真（方案 B）**：Splendor 28 个独立 face-up 发展卡卡位各有一张真卡模板/扫描件；`content/games/splendor/card_registry.json` 是真卡身份表，`check_card_identity_v2.py` 检查任一 state 内不出现两张同一真卡。Android 真机已验证新内容版本可下载并正常播放 110 cue。

## 当前优先待办

1. **Splendor full 重新过动画（扫描件已补，下一步就是这一项）**
   - 28 个 face-up 发展卡卡位已有独立真卡扫描件/模板；`check_card_identity_v2.py` 曾在 `3e979b1` 通过，但 `22c080c`/`16c18ce` 改动反例 cue 后出现 91 条失败（见待办 7）。
   - 现在需要从头到尾重新过一遍 Splendor full 110 cue：真实播放/真机观看，逐段确认画面、卡面、镜头、字幕和口播仍然一致。
   - 重点：cue46–110 市场/玩家发展区的卡面是否都是对应真卡、无重复；cue59 demo、cue70 补 4 白/4 红、cue102 终局补三级蓝的画面观感；之前 2 条 stage 重叠 warning 是否实际影响观感。
   - 产出逐 cue 问题清单；能当场改的改，需要用户裁决的记录待办。
2. **其余 8 款游戏 catalog / manifest**：补 `content/catalog/{game}.json` 与 `content/manifests/{game}.json`，让 Android 首页/内容更新覆盖全部游戏；Splendor 已有 v1。
3. **Android 真机端到端验收**：验证 PTT → ASR → 提问 → 回答 TTS → 回到动画/继续播放的完整链路，以及打断后回跳重播。
4. **动画收尾**：真实跑一次 TTS 增量；把 `cue_graph_v2.py` 接入 `compile_tutorial.py` 总控；建立编辑前后 compiled 自动回归断言；推进 Quick 版。
5. **动画检查遗留**：当前 `check_anim_v2.py` 报 2 条 stage 布局重叠 warning，待用户裁决调 stage 还是允许叠加。
6. **Flow Guide**：动画收口后开始，先做 Civolution 顶层 8 阶段循环 + 终局计分助手。
7. **补 Splendor 真卡扫描（等用户回家；先做这个）**
   - 目标：修 `action.nobles.source.001` 的 91 条 `check_card_identity_v2` 失败。
   - 最低只需补 4 张真卡：红 L1 / 红 L3 / 黑 L1 / 黑 L3 各 1 张。
   - 另有 4 处 B 区占位卡可换成已扫描/已登记模板，不需要新图；精确清单与替换关系见下方〈待补充 Splendor 真卡清单〉。

## 待补充 Splendor 真卡清单（等用户回家，2026-10-06）

**背景**：`action.nobles.source.001`（偷贵族反例）里给玩家 B 补齐 3 白 + 3 红 + 3 黑发展区时，7 张卡复用了市场已有的真卡模板，导致同一物理卡在同一状态出现两次；`check_card_identity_v2` 按每个中间快照累计报出 91 条。当前该 cue 的最终状态需要 10 张红卡位、10 张黑卡位，而 registry 只有各 8 个身份，所以最低补 4 张真卡即可同时解决容量和重复。

### 需要新扫的卡（4 张）

| 新模板 ID | 真卡（等级 / 奖励） | 声望 | 造价 | 建议文件 |
|---|---|---:|---|---|
| `card_l1_ruby_34` | 一级红 34 | 0 | 黑 1、蓝 1、绿 1、白 2 | `一级发展卡_红_34_cutout.png` |
| `card_l3_ruby_87` | 三级红 87 | 3 | 黑 3、蓝 5、绿 3、白 3 | `三级发展卡_红_87_cutout.png` |
| `card_l1_onyx_1` | 一级黑 1 | 0 | 蓝 1、绿 1、红 1、白 1 | `一级发展卡_黑_1_cutout.png` |
| `card_l3_onyx_71` | 三级黑 71 | 3 | 蓝 3、绿 5、红 3、白 3 | `三级发展卡_黑_71_cutout.png` |

备选（保持同组一张 L1 + 一张 L3 即可）：红 L1 34/36/39，红 L3 87/89/90，黑 L1 1/2/4/5/6/7/8，黑 L3 71/72/73。

### B 发展区替换映射（不需要新图的部分）

| 现在 B 区的模板 | 替换为 | 新图 | 说明 |
|---|---|---|---|
| `card_l1_diamond_17` | 保留 | 否 | 当前不与其他区域冲突 |
| `card_l2_diamond_55` | `card_l1_diamond_22` | 否 | 22 已有扫描；顺带消除 B 与 `deck_level_2` 的面朝下重复 |
| `card_l3_diamond_81` | `card_l3_diamond_80` | 否 | 80 已有扫描；81 留给 A 从市场买走 |
| `card_l1_ruby_38` | `card_l1_ruby_34` | 是 | 新扫；避开市场 L1 红 38 |
| `card_l2_ruby_70` | `card_l2_ruby_69` | 否 | 69 已有扫描 |
| `card_l3_ruby_88` | `card_l3_ruby_87` | 是 | 新扫；避开市场 L3 红 88 |
| `card_l1_onyx_908` | `card_l1_onyx_1` | 是 | 新扫；避开市场 L1 黑 908 |
| `card_l2_onyx_45` | `card_l2_onyx_909` | 否 | 909 已登记；确认该 state 未占用 |
| `card_l3_onyx_74` | `card_l3_onyx_71` | 是 | 新扫；避开市场 L3 黑 74 |

### 扫描补完后要做的实现步骤

1. 图片放入 `content/games/splendor/media/card/`：原图 + `_cutout.png`。
2. 把 4 个新模板补进：
   - `content/games/splendor/card_registry.json`
   - `content/games/splendor/card_facts.json`
   - `content/games/splendor/tutorial/anim/v2/_stage/*.stage.json`（至少 `splendor.table.stage.json`，如需近景再补其它 stage）
3. 改 `full.anim.json` 中 `action.nobles.source.001` 的 setup `create` 事件、`script.enter`/`exit` 契约；同步更新该 cue 的 QA 问题与 `_qa` 留档。
4. 重编译 `full.compiled.json`，并依次跑：
   - `python3 animation/check_card_identity_v2.py --game splendor --track full`
   - `python3 animation/validate_anim_rules_v2.py --game splendor --track full`
   - `python3 animation/check_anim_v2.py --game splendor --track full`
   - `python3 animation/audit_anim_v2.py --game splendor --track full`

## 已知未做 / 未闭环

- Splendor 发展卡扫描件/真卡身份已补齐，但**还没有重新逐 cue 过动画**；结构/state/真机加载已通过，画面观感待本轮重审。
- Unity 视觉验收此前被用户主动跳过；观感仍靠截图迭代，正式视觉验收未闭环。
- Android UaaL 与 Compose 代码已有；完整真机范围（店内平板规模、PTT → 回答 → 回到动画、打断后回跳重播）待复测，不能写成已验收。
- 仅 Splendor 有 catalog / manifest；其余 8 款待补。
- 语义检索仍需重建 Qdrant 索引；规则文件本身由 `RulesDocumentStore` 自动刷新，无需重新启动 API 服务。
- 动画 Quick 版未开始；真实增量 TTS、cue_graph 总控接入、编辑回归自动化仍待完成。

## 当前不建议做

- 在没有具体交付阻塞的情况下继续扩本体概念。
- 在动画闭环正式验收前继续做 full 的细节调优；优先 Quick 版的最小可玩路径。
- 为了“以后可能有用”继续扩展编译器能力，除非能证明它降低新增游戏或新增 cue 的边际成本。
