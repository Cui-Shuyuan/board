# 讲规动画生产说明（Splendor）

> LLM/人编写动画前先读 [v2/LLM-ANIMATION-GUIDE.md](v2/LLM-ANIMATION-GUIDE.md)。
> 数据模型细节与字段说明见 [v2/README.md](v2/README.md)。

源模型为 **section-first + reusable materials**。共享局面/动作/表现写在 `../library/`，track 的 cue 通过 `use`/`example` 引用。

## 数据链路

```text
tracks/{full,quick}.track.json                    section 顺序与 track 元信息
  └─ sections/<id>/section.json                   section 元信息 / 默认 entry / tree / tts_asset
      sections/<id>/{track}.script.json            口播文字 / beats / refs（文字唯一编辑源）
      sections/<id>/{track}.anim.json              动画源：script / tree / contract / events / camera
      sections/<id>/qa.json                        可选外置手写 QA
../library/actions|examples|presentations        tutorial-material/v1 共享动作/示例/表现
shared/stage/*.stage.json                          跨 section 的 zone / template / 机位
        │
        ▼  animation/section_source.py
tutorial/build/{track}.{script,anim}.json          装配中间产物（不进 Git）
        │
        ▼  animation/compile_tutorial.py
tutorial/{track}.runtime.json                      音频 / 字幕 / 导航元数据（构建输入与整轨兼容回退）
tutorial/anim/v2/{track}.compiled.json             Unity 只读 compiled
        │
        ├─ cue-timeline (quick/full):
        │   tutorial/{quick,full}.audio-slices.json    唯一固定时间轴
        │   tutorial/{quick,full}.timing-policy.json   仅全局流水线配置
        │   tutorial/{quick,full}.cue-timeline.json    Android/Unity 共用 plan
        │
        ▼  Unity TutorialCuePlayer / TutorialAnimPlayer
```

> quick 与 full 使用固定 audio-slices 和共享 cue plan。所有时间戳、
> source range、字幕 refs、per-cue timing policy 和 visual mapping 都只在
> `{track}.audio-slices.json`；`{track}.timing-policy.json` 仅保留全局配置。
> source 边界契约检查连续区间与显式留空。Unity 按课程 DSP 时钟输出连续
> 音频块，并在 cue 标记处同步字幕和视觉。
> [制作流程](../../../../../docs/tutorial/audio-timeline-pipeline.md) ·
> [数据协议](../../../../../docs/tutorial/cue-timeline-protocol.md) ·
> [播放器](../../../../../docs/tutorial/continuous-cue-audio.md)

## 生产顺序

1. 先查 `../library/` 是否已有可复用局面/动作/表现；能共享的不要复制到 full/quick。
2. 改 `sections/<id>/{track}.script.json` 的口播文字 / cue 切分，再改同 section 的 `{track}.anim.json` 的 `script`、契约、`use`/`example` 与 `events`。
3. 对改到规则事实的 cue，按改动点手写最小合法性 QA，优先写在该 cue 的 `qa` 字段；历史外置问题放 `sections/<id>/qa.json`。改 events/state/contract 后 QA 必须同步改。
4. 跑总控编译：`python3 animation/compile_tutorial.py --game splendor --track quick`（或 `full`）。
5. 跑 section 解析、素材 contract/facts、compiled、规则、守恒与卡身份检查。
6. Unity 采样对账、截图视觉验收通过后再定稿；每条调整按约定写 `docs/animation-change-log/entries.jsonl`。

## 检查命令

```bash
# 1. 共享素材与 section 源解析
python3 animation/material_cli.py validate --game splendor
python3 animation/check_materials.py --game splendor --track quick
python3 animation/section_source.py --game splendor --track quick --check

# 2. 编译并检查 compiled 是否最新
python3 animation/compile_animation_v2.py --game splendor --track quick
python3 animation/compile_animation_v2.py --game splendor --track quick --check

# 3. v2 契约 vs 编译快照
python3 animation/check_anim_v2.py --game splendor --track quick

# 4. Splendor 规则过账 / 跨 cue 守恒 / 卡身份
python3 animation/validate_anim_rules_v2.py --game splendor --track quick
python3 animation/audit_anim_v2.py --game splendor --track quick
python3 animation/check_card_identity_v2.py --game splendor --track quick

# 5. Unity C# 编译
python3 tools/ops/check_unity_scripts.py
```

当前编译基线以 track/section 源与 `anim/v2/{full,quick}.compiled.json` 为准；两轨均使用固定 audio-slices + 共享 plan，full 为 83 cue，quick 为 32 cue。

共享素材工作流与影响范围：见 `docs/tutorial/animation-materials-authoring-guide.md`。
