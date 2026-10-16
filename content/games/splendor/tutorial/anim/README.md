# 讲规动画生产说明（Splendor）

> LLM/人编写动画前先读 [v2/LLM-ANIMATION-GUIDE.md](v2/LLM-ANIMATION-GUIDE.md)。
> 数据模型细节与字段说明见 [v2/README.md](v2/README.md)。

当前为 **section-first** 源模型，旧的 `script.full.json` / `anim/v2/full.anim.json` / `anim/v2/_stage/` 已删除。

## 数据链路

```text
tracks/{full,quick}.track.json                    section 顺序与 track 元信息
  └─ sections/<id>/section.json                   section 元信息 / 默认 entry / tree / tts_asset
      sections/<id>/{track}.script.json            口播文字 / beats / refs（文字唯一编辑源）
      sections/<id>/{track}.anim.json              动画源：script / tree / contract / events / camera
      sections/<id>/qa.json                        可选外置手写 QA
shared/stage/*.stage.json                          跨 section 的 zone / template / 机位
        │
        ▼  animation/section_source.py
tutorial/build/{track}.{script,anim}.json          装配中间产物（不进 Git）
        │
        ▼  animation/compile_tutorial.py
tutorial/{track}.runtime.json                      音频 / 字幕 / 时长
tutorial/anim/v2/{track}.compiled.json             Unity 只读 compiled
        │
        ▼  Unity TutorialCuePlayer / TutorialAnimPlayer
```

## 生产顺序

1. 改 `sections/<id>/{track}.script.json` 的口播文字 / cue 切分，再改同 section 的 `{track}.anim.json` 的 `script`、契约与 `events`。
2. 对改到规则事实的 cue，按改动点手写最小合法性 QA，优先写在该 cue 的 `qa` 字段；历史外置问题放 `sections/<id>/qa.json`。改 events/state/contract 后 QA 必须同步改。
3. 跑总控编译：`python3 animation/compile_tutorial.py --game splendor --track quick`（或 `full`）。
4. 跑 section 解析、compiled、规则、守恒与卡身份检查。
5. Unity 采样对账、截图视觉验收通过后再定稿；每条调整按约定写 `docs/animation-change-log/entries.jsonl`。

## 检查命令

```bash
# 1. section 源解析
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

当前基线：full 67 cue / 12 section / 338 anchors；quick 24 cue / 11 section / 106 anchors。`check_anim_v2` / `validate_anim_rules_v2` 两轨均 0 warnings；full `audit_anim_v2` 53 cues 0 errors、quick 17 cues 0 errors。
