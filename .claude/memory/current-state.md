---
name: current-state
description: 新会话入口——截至 2026-09-30 的当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

> 最后更新：2026-09-30 · 基线 HEAD `2493f3e` · 后端 xUnit 75/75

# 当前状态（2026-09-30）

## 一句话

Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` god class 已完成服务化拆分，75 条 xUnit 全绿；当前工程活跃面还包括 Android UaaL 客户端、内容更新 v1、语音问答 v1；动画 full 已进入收尾/暂停状态，Flow Guide 是动画收口后的下一产品方向。

## 当前工作区状态

- 工作区干净，HEAD = `1a636be feat(anim): give Splendor development cards unique physical identities`。
- 2026-09-23 记录的动画待收口项已经由后续提交收口（time_anchors 见 `38971d4`）。
- `.claude/archive/memory/2026-09-23/` 只用于追溯历史，不作为现状依据。

## 当前已具备的能力

- **后端服务化**：`GameRulesService` 已是薄 facade，只负责构造协作类、public wrapper、`Dispose`、`ClearDerivedCaches`；业务拆为 `RulesContentStore` / `RulesConceptCatalog` / `RulesNameIndexService` / `RulesSearchService` / `RulesIndexService` / `RulesFlowService` / `RulesReferenceService` / `RulesFactService` / `RulesPlanService`。后端 xUnit 测试 75/75 全绿。
- **规则数据与检索**：9 款游戏目录，8 款有 `flow.json`，Splendor 是 Runtime + 动画试点；Qdrant + `bge-base-zh-v1.5` ONNX 检索链可用；`validate_rules.py --errors-only` 为 0 errors / 72 warnings；`tools/qa/retrieval_gold.jsonl` 85 条。
- **动画 tree/状态模型（2026-09-30 修正）**：tree 只决定 stage/可见性，不再作为状态边界；cue 默认按 `entry -> parent -> 轨道前一条` 继承完整状态，允许跨 tree。当前 stage 缺少的 zone 不渲染，组件仍保留在逻辑状态中，切回对应 tree 后恢复显示。cue46 默认继承主桌状态，cue47 在 `cards_intro` 树里继续样卡，市场不再泄漏进样卡特写。
- **动画全脚本继承关系（2026-09-30）**：tree 切换默认继承上一条 cue 的完整状态；仅在盒面/卡牌/宝石/贵族/标记等显式 demo 返回点用 `entry` 跳回 canonical 快照。卡牌 setup 现在显式销毁继承来的三张样卡背，不再靠 tree 重置；cue66 去掉重复 `entry`。
- **规则文件 freshness**：`RulesDocumentStore` 按文件 `Length + LastWriteTimeUtc` 自动失效；改规则 JSON 无需重新启动 API 服务；`ClearDerivedCaches()` 会清名称索引、Plan 类型缓存、Flow 位置缓存、Fact score 缓存；语义检索仍需要重建 Qdrant 索引（`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all` 或 `python tools/indexing/rebuild_index.py ...`）。
- **Android 客户端**：已有 Unity as a Library（UaaL）原生 Android 壳、Kotlin + Jetpack Compose 控制层、首页游戏目录/搜索/历史/资源管理、manifest → 本地内容仓库 → 增量下载/断点续传 v1、教程播放器 Compose 控制层与 Unity 状态回传、问答面板、按住说话 PTT、ASR、回答 TTS、自动播放/重播/继续播放。已有 6 个 Android JVM 测试文件（ContentStatusTest、ContentUpdaterTest、HomeContentCoordinatorTest、PlayerSessionControllerTest、QaVoiceControllerTest、UnityLoadQueueTest）；真机结论只保留已有记录部分，完整范围待复测。
- **语音链路**：后端运行时走 Python 短进程桥 `tools/voice/asr_once.py` / `tools/voice/tts_once.py`，对外接口 `POST /api/asr/once`、`POST /api/tts`；默认 TTS provider 是 `standard`（豆包标准语音合成小模型 v1），`--provider seed2` / `DOUBAO_TTS_PROVIDER=seed2` 可切回旧 2.0；standard 路径没有字级 subtitle，旧 seed2 路径有；已存在 Splendor full 音频 manifest 来源为 seed2（`zh_female_vv_uranus_bigtts` / `seed-tts-2.0`）。Android 不直接接触火山凭证，密钥只在仓库根 `.env`（git-ignored）。
- **Catalog / Manifest**：`content/catalog/splendor.json` 已入 Git；`content/manifests/splendor.json` 为生成物、不入 Git（已由 `.gitignore` 排除），当前只有 Splendor 一套。`/api/catalog/games` 是 Android 首页来源，`/api/content/games/{game}/manifest` 和 `/api/content/games/{game}/files/...` 提供 manifest 拉取与内容文件。
- **动画 full**：Splendor full 约 110 cue，`time_anchors` 已全量迁移并 commit（`38971d4`）；cue47 的样卡/桌面可见性问题已按 tree/stage 机制修正（`7ad18e5`）；源数据保留 anchor，compiled 输出数值 `at`。口播 QA 问题可/优先与 cue 同置（`full.anim.json` 的 `qa` 字段），历史问题仍在 `_qa/questions.json`；`qa_anim_ask.py` 可直接提取并自动发送，`compile_tutorial.py --validate-qa` 当前从 `_qa/questions.json` 选受影响 cue 做门禁、`--validate-qa-all` 跑全集。full TTS/runtime/compiled/Unity 链可运行。
- **发展卡身份保真（方案 B）**：Splendor 28 个独立 face-up 发展卡卡位各有一张真卡模板/扫描件；`content/games/splendor/card_registry.json` 是真卡身份表，`check_card_identity_v2.py` 检查任一 state 内不出现两张同一真卡。Android 真机已验证新内容版本可下载并正常播放 110 cue。

## 当前优先待办

1. **Splendor full 重新过动画（扫描件已补，下一步就是这一项）**
   - 28 个 face-up 发展卡卡位已有独立真卡扫描件/模板，`check_card_identity_v2.py` 已通过。
   - 现在需要从头到尾重新过一遍 Splendor full 110 cue：真实播放/真机观看，逐段确认画面、卡面、镜头、字幕和口播仍然一致。
   - 重点：cue46–110 市场/玩家发展区的卡面是否都是对应真卡、无重复；cue59 demo、cue70 补 4 白/4 红、cue102 终局补三级蓝的画面观感；之前 2 条 stage 重叠 warning 是否实际影响观感。
   - 产出逐 cue 问题清单；能当场改的改，需要用户裁决的记录待办。
2. **其余 8 款游戏 catalog / manifest**：补 `content/catalog/{game}.json` 与 `content/manifests/{game}.json`，让 Android 首页/内容更新覆盖全部游戏；Splendor 已有 v1。
3. **Android 真机端到端验收**：验证 PTT → ASR → 提问 → 回答 TTS → 回到动画/继续播放的完整链路，以及打断后回跳重播。
4. **动画收尾**：真实跑一次 TTS 增量；把 `cue_graph_v2.py` 接入 `compile_tutorial.py` 总控；建立编辑前后 compiled 自动回归断言；推进 Quick 版。
5. **动画检查遗留**：当前 `check_anim_v2.py` 报 2 条 stage 布局重叠 warning，待用户裁决调 stage 还是允许叠加。
6. **Flow Guide**：动画收口后开始，先做 Civolution 顶层 8 阶段循环 + 终局计分助手。

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
