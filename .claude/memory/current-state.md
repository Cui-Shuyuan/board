---
name: current-state
description: 新会话入口——截至 2026-09-23 的当前进度、工作区状态、待办与不做事项
metadata:
  type: project
---

# 当前状态（2026-09-23）

## 一句话

Runtime 规则问答已跑通；当前重心是《璀璨宝石》讲规动画 v3 生产闭环收尾。动画告一段落后进入 Flow Guide。

## 当前工作区状态（未提交）

2026-09-23 工作区有动画相关改动，尚未 commit：

- `time_anchors` 全量迁移：事件从裸 `at` 改为 `anchor + offset`，编译产物仍输出数值 `at`。
- Unity 侧新增 `ZoneDebugOverlay`（未跟踪），以及 Animation/CuePlayer 相关调整。
- `games/splendor/tutorial/anim/v2/full.anim.json`、`full.compiled.json` 和多个 stage/脚本有未提交修改。
- 旧 `.claude/memory/` 长文档已归档到 `.claude/archive/memory/2026-09-23/`，本目录的新文档是当前权威。

> 改完动画数据的标准顺序：`anim_schema_v2` → `compile_animation_v2 --check` → `check_anim_v2` → `validate_anim_rules_v2` → `check_unity_scripts` → Unity 采样对账。

## 当前已具备的能力

- 规则问答：`POST /api/chat`，单工具 `execute_plan`，三层回答 tier1/tier2/tier3。
- 规则数据：9 款游戏目录；8 款有 `flow.json`，`seasons` 目前只有 concepts。
- 检索：Qdrant + `bge-base-zh-v1.5` ONNX，增量索引。
- 讲规动画：Splendor full 版约 110 cue，TTS、runtime、compiled、Unity 播放器与编译链已跑通。
- 最新规则校验：`validate_rules.py --errors-only` 为 0 errors / 72 warnings（主要是缺 appearance 的 W07 和孤立概念 W05）。
- 检索 gold set：`qa/retrieval_gold.jsonl` 共 85 条，覆盖全部 9 款游戏；最近记录 82/85 resolved_hit，wrong=0，no_match=0，3 条 unresolved 的 expected 都在 top3。

## 当前优先待办

1. **收口 time_anchors 迁移**：跑完整验证链，确认 compiled 除 `source_sha256` 外一致，然后 commit。
2. **真正跑一次 TTS 增量**：改一条 cue 文本，确认只生成该 cue 的 mp3/subtitle，其他 cue 不动。
3. **cue_graph 接入总控编译**：insert/delete/split/merge 后自动改 source + 增量 TTS + 重编译 + 回归。
4. **编辑回归自动化**：编辑前后对比 compiled，除受影响 cue 外所有 `start_state/end_state/camera_in/state_ops/clips` 逐字段不变。
5. **BoardAI 校验前置**：`compile_tutorial.py --validate-qa` 从可选变成默认流程。
6. **7 条 stage 布局 warning**：player/development、showcase、供应堆相邻重叠，等用户裁决调 stage 还是允许叠加。
7. **首次正式构建后**：确认 `full.tts.lrc` 的 generator 变为 `compile_tutorial.py` 的 diff 只有这一次。
8. **Flow Guide**：动画收口后开始，先做 Civolution 顶层 8 阶段循环 + 终局计分助手。

## 已知未做 / 未闭环

- Unity 视觉验收被用户主动跳过；观感仍靠截图迭代。
- Android 模块未装，尚未在真实平板上跑完整 App。
- `manifest.json` 尚未普遍落地，前端选游戏仍需补。
- PTT/Controller、打断问答到动画播放器的完整接线尚未完成。
- 改规则文件后必须手动重建索引并重启 API；`GameRulesService` 的 JSON 缓存尚无 mtime 失效。

## 当前不建议做

- 在没有具体交付阻塞的情况下继续扩本体概念。
- 在动画闭环正式验收前继续做 full 的细节调优；优先 Quick 版的最小可玩路径。
- 为了“以后可能有用”继续扩展编译器能力，除非能证明它降低新增游戏或新增 cue 的边际成本。
