# 当前仓库接入与核验

核对日期：2026-10-22。这里记录本版 prompt 的实现依据与容易误用的接口。**代码会变，执行时再次检查；下列命令是有前提的步骤，不是一键新游戏脚本。** 本轮只进行了源与接口核查，没有重新合成、编译或验收 Splendor 成片。

## 阅读与源文件定位

先按 [CLAUDE.md](../../../CLAUDE.md) 和 [项目记忆索引](../../../.claude/memory/MEMORY.md) 读取适用约定，再按需查：

| 内容 | 入口 |
| --- | --- |
| 素材分工、契约、模板 | [新游戏作者指南](../animation-materials-authoring-guide.md) |
| 当前音频与计划链 | [音频时间轴流程](../audio-timeline-pipeline.md)、[计划协议](../cue-timeline-protocol.md) |
| 编写约束、状态继承、部位与镜头 | [Splendor 动画指南](../../../content/games/splendor/tutorial/anim/v2/LLM-ANIMATION-GUIDE.md) |
| 实际 schema、装配和编译 | [schema_defs.py](../../../animation/schema_defs.py)、[anim_schema_v2.py](../../../animation/anim_schema_v2.py)、[section_source.py](../../../animation/section_source.py)、[compile_tutorial.py](../../../animation/compile_tutorial.py) |
| 不同机制的最小共享案例 | [worker_toy](../../../tests/fixtures/worker_toy/tutorial/library/README.md) |
| 调整日志规则 | [变更日志说明](../../animation-change-log/README.md) |

`content/games/<game>/tutorial/` 下：

```text
tracks/{full,quick}.track.json          排序和元信息
sections/<id>/section.json             section 元信息、entry/tree、可选 tts_asset
sections/<id>/{full,quick}.script.json  实际存在于该 section 的 track 口播与 beats
sections/<id>/{full,quick}.anim.json    对应编排、events、引用、QA
sections/<id>/qa.json                  可选外置 QA
shared/<track>.base.anim.json          worlds/trees 等共享基底
shared/stage/*.stage.json              zone、template、命名 shots
library/{actions,examples,presentations}/
checks/ledger.py                       本游戏的规则重放检查
animation/{audit,qa}-profile.json      本游戏的审计/QA 配置
```

不是每个 section 必须同时拥有两轨文件。源结构按现有 schema；不要将本流程的分镜表、制作状态或 asset 登记字段未经实现直接加进运行 JSON。

输出链为：`源 + 媒体 → runtime / compiled → 固定 audio-slices + timing-policy → cue-timeline → 可选本地 manifest/release`。`build/` 是装配中间产物。音频与扫描等被忽略的文件仍是播放必需依赖，不能因 Git 中没有而当作可省略。

## 新游戏最容易被遗漏的接入工作

| 环节 | 当前核查到的情况 | 制作 agent 应做什么 |
| --- | --- | --- |
| 首次 TTS | `compile_tutorial.py` 两种模式都会先检查 `media/tts/<track>/tts_manifest.json`；缺失就退出 | 按 `tts_doubao.py` 的真实 LRC/输出协议初始化，不假定总控从零包办；不可手编字级 timing |
| section / cue 音频 | `section.json` 中存在 `tts_asset` 会使总控选择 section 合成路径；当前 Splendor 又有 cue 音频与 overrides | 阅读 `build_section_runtime.py`、`build_tutorial_runtime.py` 及本游戏输入，明确选择并小样验证；不能只复制目录名 |
| 固定时间轴 | `build_audio_slices.py` 是从 runtime 导入固定切片的适配器；Splendor quick 有作者维护的固定映射 | 确认本轨是否适合导入，保护已审定映射；不存在的固定文件需要制作，不能直接跳到 plan 构建 |
| 规则重放 | [validate_anim_rules_v2.py](../../../animation/validate_anim_rules_v2.py) 加载本游戏 `checks/ledger.py`，要求 `Report`、`State`、`run` | 新游戏需要相应实现，并验证事件映射与状态数据覆盖其机制；无 ledger 是能力缺口 |
| 跨 cue 审计 | [audit_anim_v2.py](../../../animation/audit_anim_v2.py) 要求 audit profile，仍有宝石、黄金、等级牌堆等结构 | 核对配置能否表达本游戏的实物和补充规则；不能换名字就假定覆盖所有机制 |
| 组件身份 | [check_card_identity_v2.py](../../../animation/check_card_identity_v2.py) 只遍历 `development_card_level_*` 朝上牌，按 registry template 判断唯一 | 它不是通用组件身份校验。新游戏要适配身份/份数约束，零命中不可算成功 |
| Board API QA | [qa_anim_ask.py](../../../animation/qa_anim_ask.py) 发送手写问题，支持 profile 和 `--only` | 需要该游戏规则数据和可用 API；检查实际回答/证据，HTTP 成功不是规则验证成功 |
| Unity 采样 | [dump_anim_v2.sh](../../../animation/dump_anim_v2.sh) 含 WSL/Windows 默认绝对路径 | 核实环境后调用实际 `TutorialV2Sampler.DumpStateV2`；不能在不同机器盲跑脚本 |
| 截图/视频 | 状态采样 JSON 不等于像素截图，也不提供声音听感证据 | 检查当前可用 Editor/客户端截图、录屏与听审途径，缺少时明确列出，不假设已有通用 MP4 导出 |
| 扫描处理 | [crop_from_manifest.py](../../../tools/media/crop_from_manifest.py) 依赖特定页/manifest 布局；[ScanCutout.cs](../../../clients/unity/Assets/Editor/ScanCutout.cs) 面向圆片；旧工具可能输出 Resources | 先检查一个样本和实际输出目录，再选工具；保留原图、处理参数与预览 |

首次接入可能需要少量工具实现，不能由 prompt 替代。优先局部配置；确需改工具时用能揭示错误的正常/非法示例验证，避免以空检查器或豁免全轨完成构建。

## 命令执行顺序

以下 PowerShell 示例在仓库根目录运行。先用实际游戏与轨道填值，确认所需源已存在；对 full/quick 分别执行。运行工具前检查 `--help` 与本轮代码变化。

```powershell
$gameId = '<game>'
$trackId = 'quick'

# 源结构：section_source --check 在内存解析，不写 build/
python animation/section_source.py --game $gameId --track $trackId --check
python animation/material_cli.py validate --game $gameId

# 正式教程音色；用户已明确另选时使用其确认的值。
$env:DOUBAO_TTS_PROVIDER = 'seed2'
$env:DOUBAO_SPEAKER = 'zh_female_vv_uranus_bigtts'
$env:DOUBAO_RESOURCE_ID = 'seed-tts-2.0'

# 前提：首次媒体/manifest 已正确初始化，源码与 QA 已齐备。
python animation/compile_tutorial.py --game $gameId --track $trackId --dry-run
python animation/compile_tutorial.py --game $gameId --track $trackId --validate-qa

# 源与产物一致性、共享素材契约、规则、跨 cue 审计。
python animation/compile_animation_v2.py --game $gameId --track $trackId --check
python animation/check_materials.py --game $gameId --track $trackId
python animation/check_anim_v2.py --game $gameId --track $trackId
python animation/validate_anim_rules_v2.py --game $gameId --track $trackId
python animation/audit_anim_v2.py --game $gameId --track $trackId
```

逐条检查退出码和覆盖范围；失败后修复，不继续把旧产物当成本次成功结果。`--skip-tts` 仅适用于有效声音可复用的情况，文本需要重生成时它会失败，并不是无凭据时的占位方案。没有规则服务时记录 QA 阻塞；按可执行范围继续独立检查，不降低验收标准。

首次 TTS 的具体 LRC 行格式、beat/cue 映射和 manifest 对应关系应从当前代码读取。基础调用形式如下，两个路径必须与选定的 runtime 输入路径一致；不能仅运行此命令就认为自动完成全部映射：

```powershell
python animation/tts_doubao.py --provider seed2 --voice zh_female_vv_uranus_bigtts --resource-id seed-tts-2.0 --input <prepared-lrc> --out-dir <tts-directory> --track <track>
```

音频与计划阶段有两种路径：

1. 从 runtime 导入固定时间轴：先创建/导入基线切片，实测声音边界并审核，需要时重新导入保留测量值，再构建 plan。
2. 已有作者维护的固定映射：只更新受影响的时间点/refs/映射，测量与审核后构建 plan；不要用导入器覆盖人工设计。

```powershell
# 只适用于已选择 runtime 导入路径的轨道。
python animation/build_audio_slices.py --game $gameId --track $trackId

# 前提：固定文件已存在，ffmpeg 可用；runtime-windows 仅用于 runtime 源窗口路径。
python animation/measure_audio_boundaries.py --game $gameId --track $trackId --runtime-windows --ffmpeg <ffmpeg-path> --write --guard-seconds 0.1

# 在审核固定时间轴及其映射后执行。
python animation/build_cue_timeline.py --game $gameId --track $trackId
```

以 [音频指南](../audio-timeline-pipeline.md) 和当前工具的导入/保留策略为准。自动 PCM 边界仍需听审。默认 guard 0.1 秒、普通间隔 0.5 秒、section 末间隔 1.0 秒是现行起点，可按实际教学节奏调整，不能代替可读性验收。

按需执行的检查：

```powershell
# 针对实际问题手写 QA 后，发送并阅读留档结果。
python animation/qa_anim_ask.py --game $gameId --track $trackId --only <section/cue>

# 共享素材修改：检查两轨所有直接/间接调用者。
python animation/material_cli.py impact --game $gameId --material <material-id> --check
python animation/material_cli.py expand-cue --game $gameId --track $trackId --cue <section/cue> --anchors

# 仅在已获取对应源版本的 Unity 采样后运行。
python animation/check_anim_v2_sample.py --game $gameId --track $trackId

# 仅对其真实覆盖的 Splendor 发展卡身份运行；新游戏使用适配后的检查。
python animation/check_card_identity_v2.py --game splendor --track $trackId

python tools/ops/check_animation_change_log.py

# 完成课程与依赖验收后，按需做本地内容包；这不是远端发布。
python tools/content/build_content_manifest.py --game $gameId
```

## 本版从 Splendor 取出的具体依据

| 观察 | 参考源 | 流程中的对应要求 |
| --- | --- | --- |
| quick 把牌面、购买动作、永久折扣分别组织为教学片段 | [quick_cards 口播](../../../content/games/splendor/tutorial/sections/quick_cards/quick.script.json)、[动画](../../../content/games/splendor/tutorial/sections/quick_cards/quick.anim.json) | 分镜按教学问题与完整动作组织；不是照句号或镜头切 cue |
| 开局局面有具体市场槽位、真卡与牌堆计数 | [initial_market example](../../../content/games/splendor/tutorial/library/examples/splendor.initial_market.example.json) | 先选择确定示例，再编排两种节奏；可复用事实要有结构断言 |
| 实物尺寸有来源，部分尺寸明确为估计 | [components.json](../../../content/games/splendor/components.json) | 组件尺度独立于扫描像素，区分确认与估值 |
| 真卡贴图、价格、等级和模板身份登记 | [card_registry.json](../../../content/games/splendor/card_registry.json) | 画面、动作和台词必须指向同一对象；身份不止是颜色 |
| 固定源时间与最终课程计划独立 | [quick timing policy](../../../content/games/splendor/tutorial/quick.timing-policy.json)、[音频指南](../audio-timeline-pipeline.md) | 核对源时间/片段时间/课程时间，不能只保证 compiled 播放 |

这些是当前源的设计依据，不是对现有每条 cue 的质量认证。取样中有规则含义的展示 cue 也可能使用 `qa_exempt`；不要据此扩大豁免范围，新内容仍按“有规则断言就写 QA”的项目约定执行。

文档历史冲突的已知例子：旧指南含已归档的 time-anchor 迁移命令、旧记忆仍写 section 连续 TTS，一些 README 的 cue 计数/时长不同。不要重跑迁移或硬编码历史数量；以当前源、执行代码与真实输出核验，并在制作记录中写明使用的接口。明确的用户约定则按其授权范围处理，不因旧实现有例外就自动取消。
