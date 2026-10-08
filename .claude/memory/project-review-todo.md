---
name: project-review-todo
description: 2026-10-08 项目审查待办——运行边界、索引一致性、内容版本与验收门禁
metadata:
  type: project
---

# 项目审查待办（2026-10-08）

状态：REV-01 / REV-02 / REV-03 / REV-04 / REV-05 / REV-06 已完成；REV-07、REV-08 未实现。来自本次代码与现行文档审查，区分已确认的逻辑缺陷、并发风险与设计改进；验收通过后再勾选。代码入口与行号以实施时的代码为准。

## 审查基线与范围

- 已阅读现行文档，核对后端问答/检索/规则缓存、Android 内容更新/语音/会话、Unity 播放及动画工具链的核心路径。
- 后端服务拆分与动画 P0～P2 重构已落地，不重新列为未完成任务；旧记录见 [animation-refactor-todo.md](animation-refactor-todo.md)。
- 本次只读检查：规则库 0 errors / 72 warnings；full 编译一致性、契约检查、83 cue 规则账本、53 cue audit、569 state 卡牌身份检查通过；compile_tutorial dry-run 无变化；10 条纯 Python 状态/继承测试通过。
- 未重新执行完整后端 xUnit、Android JVM、Unity 真机与视觉验收；旧文档中的测试通过数不作为本次验收结果。
- 本次未修改业务代码、规则数据或生成物。

## P0：优先修复

### [x] REV-01 按游戏隔离旧版本清理（已确认逻辑缺陷）

- 证据：clients/android/.../content/ContentStore.kt 的 cleanupOldVersions(game) 枚举所有完整版本，保留集合却只针对当前游戏，最后删除所有不在集合中的目录。更新游戏 A 会误删游戏 B 的资源；目前只有 Splendor，尚未在多游戏真机上复现。
- 修改：清理候选必须限定所属游戏；建议版本目录按游戏隔离。保留其他游戏的 active 指针及其资源，并保护当前正在使用的版本。
- 验收：先安装 A/B，再更新 A；B 的目录、active 指针与离线播放仍有效；暂停、失败和清理重试不影响 B。
- 实施记录（2026-10-08）：采用方案 A，新布局为 `versions/{game}/{version}` / `versions/{game}/{version}.partial`；旧扁平布局 `versions/{version}/...` 提供只读兼容与安全清理，存量单游戏设备无需清数据。`cleanupOldVersions`、`deleteLocalContent`、`deletePaused`、`deleteStalePartials`、`buildReusableIndex` 均按 game 限定候选；共享 legacy 目录和其他 game 的 active root 会被跳过，partial 不进入完整版本清理候选。
- 验收记录（2026-10-08）：先写复现测试，修复前 `ContentStoreCleanupTest.cleanupForGameADoesNotDeleteLegacyGameBVersion` 因 A cleanup 删除 B 失败；修复后通过。新增多游戏 complete/partial/active、keep=2、activate A、deleteLocalContent A、legacy 共享目录等 JVM 覆盖；`gradlew.bat testDebugUnitTest` 共 8 个测试文件 69 tests，0 failures / 0 errors。未执行多游戏真机验收。

### [x] REV-02 为问答设置停止条件并贯通取消（已确认缺失）

- 证据：backend/BoardAI.Api/Services/ChatOrchestratorService.cs 的 MaxToolRounds 为 int.MaxValue；Controllers/ChatController.cs 未向 ProcessAsync 传递请求取消信号。
- 修改：配置工具轮数、总耗时、重复失败阈值及上下文预算；将取消信号贯通 LLM、计划执行与检索；达到限制时返回已有事实或明确的未完成状态。
- 验收：用假 LLM 模拟持续调用、重复错误与超时；请求在预算内结束；客户端取消后不再产生后续 LLM 请求；正常多步查询仍可完成。
- 实施记录（2026-10-08）：`LLMOptions` 新增 `MaxToolRounds=6`、`MaxRequestSeconds=60`、`MaxRepeatedFailures=3`、`MaxConversationMessages=40`；`ChatOrchestratorService` 删除 `int.MaxValue`，使用 linked `CancellationTokenSource` + `CancelAfter` 作为总预算，按轮数、连续 tool error、相同 tool name+arguments、相同 failure signature 停止，达到限制后只发一次无工具总结；`ChatController` 传递 `HttpContext.RequestAborted` 并保留 `OperationCanceledException`；`RulesPlanService.ExecutePlanAsync`、`RulesSearchService.SearchConceptsAsync`、`VectorSearchService.SearchAsync`、`GameRulesService` wrapper 增加 CancellationToken 并向下传（Qdrant 查询透传 token，关键词扫描/embedding 同步段在关键边界检查）。
- 验收记录（2026-10-08）：新增 `ChatOrchestratorServiceTests` 6 条 Fake LLM 测试，覆盖轮数上限、重复失败阈值、LLM 等待中外部取消、总超时、正常多步查询、Controller 层 RequestAborted 传递；另加 `RulesPlanServiceTests.ExecutePlanAsync_PreCancelledToken_ThrowsBeforeExecuting` 验证计划执行边界；`TMPDIR="/mnt/d/workspace/board/.tmp-dotnet" dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo` 共 82 tests，0 failures / 0 errors。未使用真实 LLM key，未做真实设备断连集成测试。
- 残余收口（2026-10-08）：区分取消语义——客户端 `RequestAborted` 仍抛 `OperationCanceledException`，仅内部总预算到点返回 `request_timeout` 的正常未完成 reply，不再让在线客户端收到 500/连接中断。新增 `MaxToolCallsPerRound=4`、`MaxTotalToolCalls=12`：单轮或总数超限时只执行上限内调用，为剩余 tool call 补齐“未执行”结果后只发一次无工具总结。新增超时语义、单轮 tool call 上限、总 tool call 上限 3 条测试；当前后端 xUnit 93/93 通过。

## P1：可靠性与验收

### [x] REV-03 统一索引重建契约与原子切换（已确认行为差异）

- 证据：tools/indexing/rebuild_index.py 同时生成全文与名称集合，条目身份包含来源；VectorSearchService.RebuildIndexAsync 只重建全文，条目身份只有 game + concept_id。名称查询可能继续读取过期数据，同 ID 不同来源也可能覆盖。
- 修改：统一两入口的提取、来源身份、模型/维度和检索文本；索引记录规则版本、模型版本和构建状态；先构建新集合、校验完整，再切换当前索引。
- 验收：同一数据由 CLI/API 生成的两类索引条目一致；重复 ID 的各来源保留；规则改名后两集合均更新；构建失败保留旧索引，查询持续可用。
- 实施记录（2026-10-08）：新增 `IndexContract` 统一 schema/版本行与 Python 兼容 UUID（点 ID = `game::source::concept_id`，name 集合追加 `_name`；不同来源同名概念不再互相覆盖）。C# `RulesIndexService` 改为直接读原始 `concepts/instances/flow` JSON，输出 `Source` 与 `NameText`，与 `rebuild_index.py` 的提取、ref stripping、name-only 文本一致；修复了同 ID 游戏概念被 `_catalog.GetConcept` 解析回本体定义的问题（新增回归）。`VectorSearchService` 每次构建 `board_{game}__v{version}` / `...__name` 两个新 collection，版本行包含 schema、game、模型目录名、维度与全部条目；写完后按精确 count 校验，再用一次 `UpdateAliasesAsync` 原子切换稳定别名 `board_{game}__active` / `board_{game}__active_name`；切换前失败会清掉临时 collection，旧别名不动；查询优先走别名，未迁移的旧主机回退 `board_{game}` / `board_{game}_name`。Python CLI 采用同命名/同 alias/同版本行，并支持 `QDRANT_URL` 覆盖。
- 验收记录（2026-10-08）：
  - 契约一致性：用同一份仓库数据分别跑 Python 与 C# 提取，对 9 款游戏（agricola / ark-nova / brass-birmingham / castles-of-burgundy / civolution / puerto-rico / seasons / splendor / wingspan）逐条比较 `(source, concept_id, type, name_zh, name_en, name_text, strip_refs(search_text))`，全部一致（splendor 156、civolution 438 等）。
  - 端到端：Splendor Python CLI 构建 `version=61472231f3d454ae` 后，C# `--rebuild-index splendor` 识别为同一版本并复用两个 collection（156 / 151），随后原子切换别名；通过 Qdrant alias 查询 `宝石` 命中 `gem`。
  - 失败保留旧索引：临时注入 name collection 同名 alias 冲突，C# 构建在已完成 full 临时 collection 后失败；失败后旧别名仍可搜索到旧概念，临时 full collection 被清理为 404。
  - 单元测试：xUnit 新增 `IndexContractTests` 3 条与 `RulesIndexService` 来源/name_text/同 ID 冲突回归，共 97/97 通过；Python animation 套件 102/102 通过（`rebuild_index.py` 依赖 ONNX/transformers，未纳入该套件）。
- 未执行：没有用“停 Qdrant / 中断 upsert”的方式做破坏性验证；上述失败注入覆盖了切换前的异常路径。

### [x] REV-04 修复动画 QA 与规则校验门禁（已确认漏检）

- 证据：compile_tutorial --validate-qa 只选择口播变化 cue，漏掉只改 events/state/contract 的情况；run_qa_gate 只读外置 questions.json，缺少匹配问题时返回成功。审查时 17 条 cue 有内置 QA，其中 9 条不在外置问题集合，外置集合另有 2 个失效 cue ID。
- 证据：tools/content/validate_rules.py 的 --errors-only 分支发现 ERROR 后仍返回 0；已用内存模拟确认。
- 修改：按 script/events/state/contract 的相关变化选择 QA；统一 cue 内 QA 与历史外置问题的读取、去重和来源；必需问题缺失时失败；错误校验返回非零。保持手写问题和独立裁判层，不自动生成答案口径。
- 验收：只改动作也触发校验；内置 QA 被执行；缺失/失效问题明确失败；含 ERROR 的规则校验退出非零，仅 warning 仍可通过；新增回归进入统一检查入口。
- 实施记录（2026-10-08）：已实现统一 QA 合并/失效检测/按 cue 变更选门禁，`validate_rules --errors-only` 已 fail closed；新增 mock 回归覆盖上述路径。真实 Board API 定向 6/6 通过；全量 43 问在不同轮次会因 `setup.gems.004`（教学临时摆 7 颗后收回）或 `action.nobles.repeat.001.1`（多贵族时序）出现 1 条可疑回答，留作 QA 文案复核，未当作代码回归。

### [x] REV-05 固定内容发布版本并收紧打包范围（已确认设计缺口）

- 证据：ContentController 的版本 URL 校验当前 manifest 后，读取可修改的 content/games/{game}，却声明一年 immutable。修改文件而未发布 manifest 时，同版本 URL 可能返回不同字节。
- 证据：build_content_manifest.content_files 递归打包整个游戏目录；本次枚举 370 个文件，包含 50 个 QA 文件和 1 个 Python 字节码文件，非运行数据会改变内容版本。
- 修改：`build_content_manifest.py` 改为显式 runtime dependency collector：`tutorial/{track}.runtime.json`、`tutorial/anim/v2/{track}.compiled.json`，以及两份 JSON 中实际引用的音频/图片/字幕；缺引用或引用到排除路径时 fail closed。明确排除 `_qa/**`、`checks/**`、`animation/**`、`*.md`、`*.py`、`*.pyc`、`__pycache__/**`、`*.lrc`、`*.tmp`、`*.log`、`*.exitstate.json`、`*.v2sample.json`、未引用媒体等。version 只由 package 内 `path + sha256` 决定。
- 修改：发布新增不可变目录 `content/releases/{game}/{version}/`。流程为计算 version → 写 `{version}.tmp` → 校验每个文件存在/size/sha256 → 原子 rename → 校验 release 与 manifest 一致 → manifest `.tmp + atomic rename` 切换。同 version 已存在但字节不一致时明确失败，不覆盖。清理策略为当前 + 最近 2 个历史 release；回滚可通过恢复旧源文件后重跑 builder 复用已校验 release。
- 修改：`ContentController` versioned route 只从 `content/releases/{game}/{version}` 读取，缺文件直接 404，不回退 mutable `content/games`；旧 `/files/{**filePath}` 兼容路由保留并从 `content/games` 实时读取、返回 no-cache，不参与 immutable 承诺。
- 修改：catalog/manifest/Android 增加 `rules_ready` / `tutorial_ready` / `tutorial_tracks`；Android 规则-only 游戏显示“仅规则问答”，点击直接打开问答面板，不显示教程下载/更新/播放入口。旧缓存仍按 `tutorial_track` 兼容推断。
- 实施记录（2026-10-08）：
  - Splendor 实际清点从旧 366 files / 133,132,243 bytes 降为 133 files / 85,824,919 bytes；package 为 83 个 TTS mp3、46 个 card 运行图、`media/box.png`、`media/marker/...clean_cutout.png`、`tutorial/full.runtime.json`、`tutorial/anim/v2/full.compiled.json`。旧包中 QA 日志、`checks/ledger.py`/`.pyc`、anim 源/stage/schema/sample、README/guide、`.lrc`、未引用 raw 扫描图等均被排除。
  - 实际 release `content/releases/splendor/1964d531eedcae5f/` 133 files；重复发布同 version 幂等复用，校验通过。
- 验收记录（2026-10-08）：
  - Python：`python3 -m unittest discover -s animation -p 'test_*.py'` 99 tests，0 failures；新增 7 条 builder 回归覆盖 QA/pyc/文档不改 version、runtime 改动改 version、只含白名单、发布失败保留旧 manifest/release、重复同 version 不同内容失败。
  - C#：`dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo` 89 tests，0 failures；新增 ContentController 5 条（release 200+immutable、source 修改仍回 release 旧字节、version mismatch 409、缺文件 404、release 目录缺失不回退）和 CatalogController 2 条（规则-only 不暴露下载元数据、旧 `tutorial_track` 兼容推断）。
  - Android：`gradlew.bat testDebugUnitTest` 73 tests，0 failures；新增 rules-only 状态/点击不进教程、直接打开问答、禁止下载等回归。
  - Python：`python3 tools/content/validate_rules.py --errors-only` 0 errors / 72 warnings；`git diff --check` 通过。
  - 未执行：真实 Android 设备/界面验收；规则-only 首页直接问答已由 JVM 状态层覆盖，但未做真机端到端。
- 残余收口（2026-10-08）：catalog 省略 `rules_ready` 时不再默认 true，改为按 `content/games/{id}/concepts.json` 是否存在/非空推导，显式 catalog 声明仍优先；规则-only 模式 QaPanel 关闭按钮改为“返回”，教程模式保持“继续播放”。`build_content_manifest._walk_references` 对显式 file-reference key 的非媒体路径不再静默忽略：`.subtitle.json` 等实际存在的运行时文件会被打包，缺失、越界或落在排除目录时 fail closed。README 与 `docs/ops/start-services.md` 明确 `content/manifests` / `content/releases` 不入 Git，生产 API 主机需本机构建或单独部署。当前 Python 102/102、xUnit 93/93、Android JVM 75/75。
### [x] REV-06 隔离新旧问答会话及 ASR 结果（已确认竞态路径）

- 证据：QaPanel 在请求期间允许新建会话，旧请求返回后直接向当前 QaSessionHolder 追加；QaVoiceController 的 ASR 无会话代际检查。TTS 已有 generation，可作为实现参考。
- 修改：会话 ID/代际贯通 chat、ASR、TTS；跟踪并取消任务，提交结果前确认所属会话与请求；取消阻塞网络时关闭连接；清理录音/回答音频的生命周期。
- 验收：旧请求延迟返回、新建会话、切换游戏、返回首页与连续录音交错时，旧结果不进入新会话、不覆盖新输入、不意外播音；当前有效结果正常显示。
- 实施记录（2026-10-08）：`QaSession` 增加 holder 分配的 generation；`QaSessionHolder` 的 `appendMessage` 必须携带 generation，active session 变化后旧回调直接拒绝；切换游戏与 clear 后旧 generation 全部失效。QaPanel 为每次发送保留 session generation、active chat Job 和 stale 检查，新建会话/关闭面板/切换游戏时取消 Job 并断开 `QaRepository` 阻塞连接；旧 reply/error 不写当前消息、不改输入、不启动 TTS。QaVoiceController 为 ASR/TTS 增加 generation、Job 跟踪与引擎级 `cancelActiveRequests()`：新会话/新 PTT 会取消旧录音、ASR、TTS 并断开 HTTP 连接，迟到的转写/合成结果被忽略，临时 wav 与回答音频在会话结束/关闭时清理；MainActivity 在 close/return/switch 路径同步断开 chat 连接。
- 验收记录（2026-10-08）：新增 `QaSessionHolderTest` 4 条（同游戏上下文更新保持 generation、新会话清空并换代、stale append 拒绝、切换游戏失效旧 owner），`QaVoiceControllerTest` 新增 4 条（新会话丢弃迟到 ASR 并删 wav、新 PTT 丢弃前一录音 ASR、PTT 取消迟到自动 TTS、启动新会话删除缓存回答音频），`QaRepositoryTest` 1 条（cancelActiveRequests 断开阻塞 chat 请求并抛 cancellation）。Android JVM `:app:testDebugUnitTest` 共 84 tests，0 failures / 0 errors。未执行真机端到端；JVM 覆盖的是状态机与连接取消，未覆盖 MediaPlayer/AudioRecord 驱动行为。

### [x] REV-07 规则缓存并发与版本一致性（代码显示风险，需补并发复现）

- 证据：GameRulesService 为 singleton，名称/流程/类型等派生缓存使用普通 Dictionary，读取、写入与 Clear 缺少共同同步；热更新可交错重建。RulesDocumentStore 将被替换文档加入 retired，直到服务 Dispose 才释放。
- 修改：按规则版本构建不可变快照并原子切换；一次请求绑定一个版本，名称、流程、事实使用同一快照；通过明确所有权/引用生命周期释放旧文档。仅换成 ConcurrentDictionary 不足以保证整次请求一致性。
- 验收：多请求与连续热更新交错，无并发异常或混合版本；新增/删除文件和缓存命中路径能刷新；长期更新后旧快照内存可回收。
- 实施记录（2026-10-08）：新增 `RulesGameSnapshot`（按游戏深拷贝本体/游戏 concepts/instances/flow，带内容哈希 Version）与 `RulesSnapshotScope`（AsyncLocal 请求作用域）。`RulesContentStore.GetSnapshot(game)` 在 store 锁内探测文件增删改、按 Revision 缓存并对旧快照自然换出；快照一旦被请求捕获，整条调用链（包含 `RulesSearchService` 的 Task.Run 子任务）读取同一版本。名称/精确名/流程/Plan 类型/计分表缓存均改为“快照内一次性构建”，普通 Dictionary 路径只保留给直接构造协作类的旧单测。`RulesDocumentStore` 每次实际加载/替换/删除推进 Revision；retired 原始 JsonDocument 改为有界队列（最多 16 个），读取增加短暂重试；快照使用独立 `JsonDocument`，不再依赖 retired 生命周期。`ChatOrchestratorService` 在整轮 Chat 进入 `BeginRequestScope`，同一轮内多个 execute_plan/注解也不会跨版本。
- 验收记录（2026-10-08）：新增 `Rev07RulesSnapshotTests` 4 条：200 次热更新后 retired 文档数有界；真实文件新增/删除 game concepts 后快照刷新；4 个并发请求与 500 次内存热更新交错无异常、无单请求混合版本；缓存命中后一次内存更新即重建派生缓存。修复前临时复现已观察到 retired count 200、`RulesFlowService` 并发 `IndexOutOfRangeException`/文件 `IOException` 与版本混用；修复后 `dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo` 101/101 通过。

### [x] REV-08 逐查询记录回答证据（设计改进）

- 证据：AnswerEvidence 只要一次查询有 ok + Matched，就将整条回答标为 tier1；复合问题中查不到的部分被掩盖；ChatResponse 只有 reply。
- 修改：逐查询记录规则版本、命中来源、引用与未解决项，响应显式携带；部分缺失时清楚区分已查到事实与待确认信息。证据元数据用于追溯与评测，不视为对 LLM 最终每句话的自动真实性证明。
- 验收：复合问题一部分命中、一部分 no_match 时不会整体标为完全有据；客户端和日志可定位缺失项；正常规则回答可追溯到对应数据版本。
- 实施记录（2026-10-08）：`ChatResponse` 新增可空 `Evidence` 字段（`[JsonIgnore(WhenWritingNull)]`，旧客户端忽略），`Reply` 保持原字段。证据模型为 `AnswerEvidence` / `QueryEvidence` / `EvidenceConcept`：逐 query 记录 relation、entity、status、source、规则版本、message、matched、candidates、一层 references 与 flow_context。`PlanExecutionResult` 新增 `RulesVersion`，由 `RulesPlanService` 从请求 scope 的规则快照写入；Chat 整轮复用同一 `RulesVersion`。`ChatOrchestratorService` 改为逐条 execute_plan 结果累计证据，不再“看到一次 Matched 即停止”；最终 tier 按整组 query 判定：全部 ok 且有数据=tier1，有数据但有 unresolved/no_match/unsupported=partial，仅候选=tier2，无数据=tier3；日志同步输出 partial。Controller 通过新增 `ProcessWithEvidenceAsync` 返回 Reply+Evidence，`ProcessAsync` 保留为只取 Reply 的兼容入口。证据只用于追溯/评测，不宣称 LLM 每句话自动为真。
- 验收记录（2026-10-08）：新增 `Rev08AnswerEvidenceTests` 5 条：正常命中 tier1 且 RulesVersion/source/matched 可追溯；复合 explain 命中 + no_match 为 partial 且缺失 query 带 message；identify 仅候选为 tier2；Controller 返回 Reply+Evidence；无 Evidence 的 ChatResponse 序列化仍只有 Reply。`dotnet test backend/BoardAI.Api.Tests/BoardAI.Api.Tests.csproj --nologo` 106/106 通过。未做真实 LLM/Android 客户端展示验收；Evidence 为附加字段，旧客户端可忽略。

## 执行与收口

- 建议顺序：REV-01 → REV-02 → REV-03 → REV-04 → REV-05 → REV-06 → REV-07 → REV-08。REV-01 完成后再开放多游戏资源。
- 每项以触发条件、实际修改和验收记录收口，风险项先补复现；不因其他 checker 全绿而自动勾选。
- [ ] 同步过期文档：current-state 已撤下完成的跨平台 path bug 待办；后续更新索引入口、QA 门禁和缓存语义的文档，统一“问答后重播/精确恢复”的产品行为说明。
- [ ] 完成已有交付验收：full 逐 cue 视觉重审、PTT → ASR → chat → TTS → 回到动画，以及多设备长时间使用；范围见 [current-state.md](current-state.md)。

## 已知问题解决后的工作优先级（建议，非已批准排期）

前提：以上缺陷与已有 full/语音/真机验收缺口已闭环；以下是新增产品与推广工作，不把已完成修复重新列为任务。

1. **Splendor Quick 版**：围绕摆好桌面、看懂一回合、完成首次购买与知道终局编排最小教案；复用现有原语，新增 cue 先由用户审核。验收以首次接触的玩家能开始游戏为准，记录讲解时间与需要店员补充的内容。
2. **小范围店内试点**：从少量真实桌次收集首次开局时间、人工介入次数、问答等待时间与未解决问题。据此决定内容与交互投入，形成可重复发布、部署与使用的操作流程。
3. **Civolution Flow Guide MVP**：沿已确定方向，先做顶层 8 阶段游标与终局计分助手；程序控流程/算分，玩家提供条件和数量；支持不确定时进入问答并返回原节点。以真实一局的流程与计分验收，详见 [flow-guide.md](flow-guide.md)。
4. **验证第二款游戏的生产流程**：结合试点选择一款有需求、规则已具备的游戏，完成规则问答与 Quick 教程的整套交付；记录教案、素材、QA、编译与发布工时，以及是否必须改通用引擎。用实际边际成本决定后续扩展节奏。
