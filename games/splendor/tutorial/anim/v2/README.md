# v2 动画管线（阶段 3）

这一版把“运行时解析手写动画”改成“编译期确定所有状态/几何，运行期纯采样”。

## 源文件

- `{track}.anim.json`：手写文字脚本、树/world、契约、事件。
- `_stage/*.stage.json` 或同目录 `*.stage.json`：stage 源数据。
- 结构校验：`scripts/anim_schema_v2.py`。

## 编译

```bash
python3 scripts/compile_animation_v2.py --game splendor --track _schema_example
python3 scripts/compile_animation_v2.py --game splendor --track _schema_example --check
```

编译器负责：

- 解析 selector，分配确定的组件 id；
- 生成每条 cue 的 `start_state` / `end_state` 快照；
- 生成具体到 item_id 的 clips（spawn / move / destroy / scale / fade / highlight / point / picture）；
- 用 `scripts/anim_geometry_v2.py`（唯一几何源）生成 slot table 与 camera frame。

## 检查

```bash
python3 scripts/anim_schema_v2.py --example
python3 scripts/compile_animation_v2.py --game splendor --track _schema_example --check
python3 scripts/check_anim_v2.py --game splendor --track _schema_example
python3 scripts/check_unity_scripts.py
```

`check_anim_v2.py` 校验手写 `enter/exit` 契约与编译快照一致。

## 运行时

- C# 纯模型：`client/Assets/Scripts/Tutorial/Animation/Core/`。
- Unity 薄适配：`client/Assets/Scripts/Tutorial/Animation/Unity/TutorialAnimPlayer.cs`。
- 运行时只读 `{track}.compiled.json`，不再自己算几何、解析 selector 或重放入口链。
