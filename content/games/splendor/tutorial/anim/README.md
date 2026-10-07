# 讲规动画生产说明（Splendor）

> LLM/人编写动画前先读 [v2/LLM-ANIMATION-GUIDE.md](v2/LLM-ANIMATION-GUIDE.md)。
>
> 数据模型细节与字段说明见 [v2/README.md](v2/README.md)。

## 数据链路

```text
script.full.json                                          口播文字 / cue 切分 / beats / refs（文字唯一编辑源）
                │
animation/compile_tutorial.py                             总控：增量 TTS → runtime → compiled
  ├─ full.runtime.json                                    音频 / 字幕 / 时长
  └─ anim/v2/full.anim.json                               手写动画源：
       script.story/note/camera/enter/exit                文字与契约
       tree / transition                                  树与舞台
       events                                             原语执行层
                │
animation/compile_animation_v2.py                          确定性编译器
  animation/anim_geometry_v2.py                            唯一几何源
                ▼
anim/v2/full.compiled.json                                 Unity 只读 compiled
                ▼
Unity TutorialAnimPlayer                                  Unity 薄适配
```

## 生产顺序

1. 先改 `script.full.json` 的口播文字 / cue 切分，再改 `full.anim.json` 的 `script`、契约与 `events`；
2. 对改到规则事实的 cue，按改动点手写最小合法性 QA（cue 的 `qa` 字段或 `_qa/questions.json`）；
3. 跑总控编译：`python3 animation/compile_tutorial.py --game splendor --track full`；
4. 跑静态、编译、规则与采样检查；
5. 视觉验收通过后再定稿。

## 检查命令

```bash
# 1. 静态 schema / 文字结构与字段
python3 animation/anim_schema_v2.py content/games/splendor/tutorial/anim/v2/full.anim.json

# 2. 编译并检查 compiled 是否最新
python3 animation/compile_animation_v2.py --game splendor --track full
python3 animation/compile_animation_v2.py --game splendor --track full --check

# 3. v2 契约 vs 编译快照（静态对账）
python3 animation/check_anim_v2.py --game splendor --track full

# 4. Splendor 规则过账
python3 animation/validate_anim_rules_v2.py --game splendor --track full

# 5. C# 编译
python3 tools/ops/check_unity_scripts.py

# 6. Unity 采样 + 对账（需 Windows 工作区同步、Unity 批处理）
./animation/dump_anim_v2.sh --game splendor --track full
python3 animation/check_anim_v2_sample.py --game splendor --track full
```

当前 Splendor full 为 83 cue，检查基线为 `check_anim_v2` / `validate_anim_rules_v2` 83 cues 0 warnings、`audit_anim_v2` 53 cues 0 errors。
