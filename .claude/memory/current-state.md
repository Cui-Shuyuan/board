---
name: current-state
description: 新会话入口——截至 2026-10-08 的当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

> 最后更新：2026-10-08 · 基线见 git log · 本轮后端 xUnit 97/97、Python 102/102、Android JVM 84/84

# 当前状态（2026-10-08）

## 一句话

Runtime / 搜索 / 规则数据已跑通；后端 `GameRulesService` god class 已完成服务化拆分，97 条 xUnit 全绿；当前工程活跃面还包括 Android UaaL 客户端、内容更新 v1、语音问答 v1；动画 full 已完成终局与真卡身份收口，下一步逐 cue 重审，Flow Guide 是动画收口后的下一产品方向。

## 当前工作区状态

- 本文档基线提交为 `3a00608`；当前 HEAD 以 `git log` 为准。基线前最近提交的工作包括：Splendor 终局计分 / 合法性 / 真卡扫描收口（`be4f88c`）、合并 cue 的 seed2 TTS 重生成（`7d84df4`）、`set_order` 与静默 setup 片段（`6afd1d2`）、Android seek flash 修复（`95487f3`）。
- `full.anim.json` 的 cue id 已语义化（如 `action.purchase_reserved.001`），旧文档里的 cue 数字编号只作历史对照。
- 动画对象原语统一为 `target` 接口（`entity` / `screen`）；新增 edge flip（axis/direction/destination）、`draw` 默认 duration、`set_order`、magnifier world-view pinning 等能力。
- 本轮改动前工作区已无已跟踪文件改动；仅剩未跟踪的 `archive/tools/`。此前 5 个 Unity 编辑器回写的 Settings/ProjectSettings 文件已由 `627401d` 提交。
- Android 代码已到 `95487f3`（每次 seek 独立重置快进 flash 计时）；APK 产出路径为 `clients/android/app/build/outputs/apk/debug/app-debug.apk`。内容版本以本地生成的 `content/manifests/splendor.json` 为准，状态文档不再记某个设备上的旧版本号。
- `full` 轨道当前 83 cue / 388 个 `time_anchors`，源数据保留 `anchor`，`full.compiled.json` 输出数值 `at`。
- 2026-09-23 记录的动画待收口项已由后续提交收口（time_anchors 见 `38971d4`）；`.claude/archive/memory/2026-09-23/` 只用于追溯历史，不作为现状依据。

## 当前已具备的能力

- **后端服务化**：`GameRulesService` 已是薄 facade，只负责构造协作类、public wrapper、`Dispose`、`ClearDerivedCaches`；业务拆为 `RulesContentStore` / `RulesConceptCatalog` / `RulesNameIndexService` / `RulesSearchService` / `RulesIndexService` / `RulesFlowService` / `RulesReferenceService` / `RulesFactService` / `RulesPlanService`。后端 xUnit 测试 97/97 全绿（2026-10-08 本轮）。
- **规则数据与检索**：9 款游戏目录，8 款有 `flow.json`，Splendor 是 Runtime + 动画试点；Qdrant + `bge-base-zh-v1.5` ONNX 检索链可用；全文/name 两套索引统一为版本化 collection + 稳定 alias（`board_{game}__active[_name]`），Python CLI 与 C# API 共用 `IndexContract` 的提取/点 ID/版本行，构建校验后原子切换，失败保留旧索引；`validate_rules.py --errors-only` 为 0 errors / 72 warnings；`tools/qa/retrieval_gold.jsonl` 85 条。
- **动画 tree/状态模型（2026-09-30 修正）**：tree 只决定 stage/可见性，不是状态边界；cue 默认按 `entry -> parent -> 轨道前一条` 继承完整状态，允许跨 tree。当前 stage 缺少的 zone 不渲染，组件仍保留在逻辑状态中，切回对应 tree 后恢复显示。full 轨道现只有 `main` 一棵 tree，其它展示内容改走 screen 对象；该机制仍保留给多树轨道/其它游戏。
- **动画全脚本继承关系（2026-09-30）**：tree 切换默认继承上一条 cue 的完整状态；仅在盒面/卡牌/宝石/贵族/标记等显式 demo 返回点用 `entry` 跳回 canonical 快照。full 现在只有 main，旧展示树切换点已收口。
- **动画对象接口（2026-10-01）**：所有对象原语通过 `target` 接口选择接收者；编译器展开为 flat 字段，Unity 用 `IAnimVisualObject` 同时执行实体和屏幕对象的表现原语。源数据统一写 `show/hide`；编译产物里屏幕对象使用 `overlay_show/overlay_hide` 作为 clip kind。迁移工具 `animation/migrate_object_targets_v2.py`。
- **动画标注层（2026-10-01 收口）**：`point` / 新增 `shape`（`arrow`/`circle`/`cross`/`forbid`/`box`）/ `label` 统一编译成运行时 `FrameState.Annotations`，每条显式带 `annotation_space=world|screen`：world 锚实体并每帧投影（跟镜头/卡牌），screen 锚 overlay/屏幕槽位（不跟镜头）。`label` 新增 world 锚定；`part` 语义锚点和 mapping `offset`/`nudge` 屏幕微调已落地。
- **规则文件 freshness**：`RulesDocumentStore` 按文件 `Length + LastWriteTimeUtc` 自动失效；改规则 JSON 无需重新启动 API 服务；`ClearDerivedCaches()` 会清名称索引、Plan 类型缓存、Flow 位置缓存、Fact score 缓存；语义检索仍需要重建 Qdrant 索引（`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all` 或 `python tools/indexing/rebuild_index.py ...`）。
- **Android 客户端**：已有 Unity as a Library（UaaL）原生 Android 壳、Kotlin + Jetpack Compose 控制层、首页游戏目录/搜索/历史/资源管理、manifest → 本地内容仓库 → 增量下载/断点续传 v1、教程播放器 Compose 控制层与 Unity 状态回传、问答面板、按住说话 PTT、ASR、回答 TTS、自动播放/重播/继续播放。QA 会话已按 generation 隔离新旧 chat/ASR/TTS 结果，并在新会话/新 PTT/关闭时取消阻塞 HTTP 连接与清理音频。已有 11 个 Android JVM 测试文件（ContentStatus、ContentStoreCleanup、ContentUpdater、HomeContentCoordinator、PlayerSessionController、PlayerTimelineBar、UnityLoadQueue、QaVoiceController、QaPanelMode、QaSessionHolder、QaRepository），本轮 84/84 通过；真机结论只保留已有记录部分，完整范围待复测。
- **语音链路**：后端运行时走 Python 短进程桥 `tools/voice/asr_once.py` / `tools/voice/tts_once.py`，对外接口 `POST /api/asr/once`、`POST /api/tts`；默认 TTS provider 是 `standard`（豆包标准语音合成小模型 v1），`--provider seed2` / `DOUBAO_TTS_PROVIDER=seed2` 可切回旧 2.0；standard 路径没有字级 subtitle，旧 seed2 路径有；已存在 Splendor full 音频 manifest 来源为 seed2（`zh_female_vv_uranus_bigtts` / `seed-tts-2.0`）。Android 不直接接触火山凭证，密钥只在仓库根 `.env`（git-ignored）。
- **Catalog / Manifest**：`content/catalog/splendor.json` 已入 Git，并显式声明 `rules_ready=true` / `tutorial_ready=true` / `tutorial_tracks=["full"]`；`content/manifests/splendor.json` 与 `content/releases/splendor/{version}/` 为生成物、不入 Git，当前只有 Splendor 一套。manifest builder 已改为 runtime-only 白名单（runtime/compiled + 实际引用媒体；排除 QA/文档/脚本/pyc/lrc/动画源），version 只由 package 内 `path + sha256` 决定；显式 file-reference key 的非媒体路径会校验并实际打包（如 `subtitle_file: "*.subtitle.json"`），缺失/越界/排除路径 fail closed；发布使用 `{version}.tmp` + 逐文件校验 + 原子 rename，versioned URL 只从 release 读取，缺文件 404，不回退到 mutable source。catalog 能力字段已贯通后端与 Android：省略 `rules_ready` 时按 `content/games/{id}/concepts.json` 实际内容推导，显式声明优先；规则-only 游戏显示“仅规则问答”并直接进入问答，不显示教程下载/播放入口。生产 API 主机必须本机 build manifest/release 或单独部署这两个 git-ignored 目录。
- **动画 full**：Splendor full 83 cue（cue id 已语义化），当前 388 个 `time_anchors`，已全量迁移并 commit（`38971d4`）；早期样卡/桌面可见性问题已按 tree/stage 机制修正（`7ad18e5`）；源数据保留 anchor，compiled 输出数值 `at`。口播 QA 问题可/优先与 cue 同置（`full.anim.json` 的 `qa` 字段），历史问题仍在 `_qa/questions.json`；`qa_anim_ask.py` 可直接提取并自动发送，`compile_tutorial.py --validate-qa` 当前从 `_qa/questions.json` 选受影响 cue 做门禁、`--validate-qa-all` 跑全集。full TTS/runtime/compiled/Unity 链可运行。
- **发展卡身份保真（方案 B）**：Splendor 28 个独立 face-up 发展卡卡位各有一张真卡模板/扫描件；`content/games/splendor/card_registry.json` 是真卡身份表，`check_card_identity_v2.py` 检查任一 state 内不出现两张同一真卡（当前 569 个中间状态 0 error）。Android 真机此前已验证 83 cue 内容可下载并正常播放；合并后的终局说明同时展示玩家 A/B 双方发展区与贵族。

## 当前优先待办

> 本轮工程优先级见 [project-review-todo.md](project-review-todo.md)：REV-01/02/03/04/05/06 已完成；继续 REV-07 规则缓存并发与版本一致性、REV-08 逐查询回答证据。动画 P0～P2 重构已完成，旧验收与剩余 P3 项见 [animation-refactor-todo.md](animation-refactor-todo.md)，不要重新执行已完成项。

1. **Splendor full 重新过动画（真卡身份阻塞已解除，下一步逐 cue 重审）**
   - 4 张补扫真卡（红 34 / 红 87 / 黑 1 / 黑 71）已完成去白边、登记 registry/facts、补进 table stage，并把 `action.nobles.source.001` 的 B 区替换为专属真卡。
   - 当前检查结果：`check_card_identity_v2.py` 569 个中间状态 0 error；`validate_anim_rules_v2.py` 83 cues / 0 warnings；`check_anim_v2.py` 83 cues / 0 warnings；`audit_anim_v2.py` 53 cues / 0 error / 0 warning。
   - 需要从头到尾重新过一遍 Splendor full 83 cue：真实播放/真机观看，逐段确认画面、卡面、镜头、字幕和口播仍然一致。
   - 重点：`action.cards.*` / `action.nobles.*` / `action.reserve.*` / `action.purchase_reserved.001` 的市场与玩家发展区卡面是否都是对应真卡、无重复；贵族放大镜 demo（`action.nobles.choice.001.1`、`action.nobles.repeat.001.1`）；`setup.gems.003.2` / `setup.gems.004` 演示后 `setup.gems.005.1` 的宝石数量复位；`endgame.example.001.1` 的终局说明是否同时展示 A/B 双方发展区与贵族。
   - 产出逐 cue 问题清单；能当场改的改，需要用户裁决的记录待办。
2. **其余游戏目录与能力声明**：能力字段与 Android rules-only 路径已就绪；后续补 catalog 时必须逐游戏显式声明 `rules_ready` / `tutorial_ready` / `tutorial_tracks`，补 manifest 不等于教程可播放。目前仅 Splendor 有 runtime，实际条目仍待补。完整第二款游戏的交付优先级见 project-review-todo。
3. **Android 真机端到端验收**：验证 PTT → ASR → 提问 → 回答 TTS → 回到动画/继续播放的完整链路，以及打断后回跳重播。
4. **动画收尾**：真实跑一次 TTS 增量；把 `cue_graph_v2.py` 接入 `compile_tutorial.py` 总控；建立编辑前后 compiled 自动回归断言；推进 Quick 版。
   - manifest 路径归一化已实现；2026-10-08 当前 Windows 工作区 dry-run 为 0 changed / 0 removed / 0 ref-only，不再列为未修复 bug。
5. **动画检查现状**：`check_anim_v2.py` 83 cues / 0 warnings，`validate_anim_rules_v2.py` 83 cues / 0 warnings，`audit_anim_v2.py` 53 cues / 0 error / 0 warning；此前 2 条 stage 重叠 warning 已不再报出。
6. **Flow Guide**：动画收口后开始，先做 Civolution 顶层 8 阶段循环 + 终局计分助手。

## Splendor 真卡补扫（2026-10-07 已完成）

- 新补 4 张真卡：`card_l1_ruby_34`（红 L1 34）/ `card_l3_ruby_87`（红 L3 87）/ `card_l1_onyx_1`（黑 L1 1）/ `card_l3_onyx_71`（黑 L3 71）。
- 白边处理沿用 `tools/media/matte_pipeline.py --class card`（2mm 圆角 + 2px 收边）；4 张输出 748x1045 RGBA，`matte_eval` 4/4 PASS。
- `card_registry.json` / `card_facts.json` 已补 4 条身份；`splendor.table.stage.json` 已补 4 个模板。
- `action.nobles.source.001` 的 B 区替换：diamond_17 保留；diamond_55→diamond_22、diamond_81→diamond_80、ruby_38→ruby_34、ruby_70→ruby_69、ruby_88→ruby_87、onyx_908→onyx_1、onyx_45→onyx_909、onyx_74→onyx_71；A 从市场买走的仍是 diamond_81。
- 验证：`check_card_identity_v2` 569 states 0 error；`validate_anim_rules_v2` 83 cues / 0 warnings、`check_anim_v2` 83 cues / 0 warnings、`audit_anim_v2` 53 cues / 0 error / 0 warning；真机更新当次内容后 `action.nobles.source.001` 正常播放，设备侧文件哈希与本地一致。

## 已知未做 / 未闭环

- 2026-10-08 全项目审查的 8 项中，REV-01 / REV-02 / REV-03 / REV-04 / REV-05 / REV-06 已完成并本地提交（未 push）；REV-07（规则缓存并发与版本一致性）、REV-08（逐查询回答证据）仍未实现。已完成项的证据、残余与验收记录见 [project-review-todo.md](project-review-todo.md)。

- Splendor 真卡身份与合并 cue TTS 已收口（`check_card_identity_v2` 0 error），但**还没有从头到尾重新逐 cue 过一遍动画**；画面观感待本轮重审。
- Unity 视觉验收此前被用户主动跳过；观感仍靠截图迭代，正式视觉验收未闭环。
- Android UaaL 与 Compose 代码已有；完整真机范围（店内平板规模、PTT → 回答 → 回到动画、打断后回跳重播）待复测，不能写成已验收。
- 仅 Splendor 有 catalog / manifest；其余 8 款待补。能力模型已支持规则-only，不得再默认 `tutorial_track="full"`，补 catalog 条目也不等于教程可播放。
- 语义检索仍需重建 Qdrant 索引；规则文件本身由 `RulesDocumentStore` 自动刷新，无需重新启动 API 服务。
- 动画 Quick 版未开始；真实增量 TTS、cue_graph 总控接入、编辑回归自动化仍待完成。

## 当前不建议做

- 在没有具体交付阻塞的情况下继续扩本体概念。
- 在动画闭环正式验收前继续做 full 的细节调优；优先 Quick 版的最小可玩路径。
- 为了“以后可能有用”继续扩展编译器能力，除非能证明它降低新增游戏或新增 cue 的边际成本。
