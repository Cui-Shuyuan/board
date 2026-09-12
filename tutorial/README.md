# Tutorial 动画数据层

教学动画的数据驱动格式与校验工具。动画 = `tutorial.json` + sprite 图 + 音频，播放端只做确定性播放。

## 文件

- `tutorial/schema/tutorial.schema.json` — JSON Schema，唯一的数据契约。
- `tutorial/examples/splendor.tutorial.json` — 一份最小示例（不绑定真实素材）。
- `games/{game}/tutorial.json` — 每款游戏的教学动画数据，素材路径相对于 `games/{game}/media/`。
- `scripts/validate_tutorial.py` — 进 Unity 之前先跑它，把错误挡在程序层。
- `client/Assets/Scripts/Tutorial/` — Unity 播放端：
  - `TutorialData.cs`：与 schema 对应的强类型数据模型（JsonUtility 兼容）。
  - `Easing.cs`：缓动函数库。
  - `TutorialPrimitives.cs`：动画原语库（position/rotation/scale/alpha/shuffle）。
  - `TutorialDirector.cs`：数据驱动播放器骨架（空场景自动搭景 + 播放）。

## 校验

```bash
# 按游戏名校验（默认检查素材文件是否存在）
python scripts/validate_tutorial.py --game splendor

# 素材还没就位时，只校验结构与引用
python scripts/validate_tutorial.py --game splendor --skip-assets

# 给 LLM 看的机器可读输出
python scripts/validate_tutorial.py --game splendor --json
```

退出码：`0` 通过；`1` 有错误；`2` 文件不存在。

## 核心约定

1. **坐标**：slot 的 `x/y` 是版图归一化坐标。`board.origin=top_left` 时 `(0,0)` 是版图左上角，`x` 向右、`y` 向下；`origin=center` 时 `(0,0)` 是版图中心。允许 `x/y` 超出 `[0,1]`，表示版图外的位置（如游戏盒）。
2. **动作原语**：时间轴事件只允许 8 个 `action`：
   `move`、`flip`、`rotate`、`scale`、`fade`、`highlight`、`shuffle`、`wait`。
   各动作需要的字段由 schema 和 validator 强制检查。
3. **sprite 与 slot 分离**：`sprites` 只描述外观（文件 + 物理尺寸），`slots` 只描述逻辑位置。事件用 id 引用两者。
4. **音频**：每章一个 `audio`，字幕 `subtitles` 按 `t` 升序；打断后从当前章节重播。
5. **缺省值**：`easing`、`pivot`、`origin`、`pixels_per_unit` 都可省略，播放端会补齐。

## Unity 播放端接入

`TutorialDirector` 通过 `RuntimeInitializeOnLoadMethod` 在空场景启动，读取顺序：

1. `tutorialRoot` 参数指定的目录（如 `D:\workspace\board\games`，编辑器调试时填这个）；
2. `Application.persistentDataPath/{gameId}`；
3. `Application.streamingAssetsPath/{gameId}`。

目录结构要求：

```
{root}/{gameId}/
  tutorial.json
  media/
    board/board.png
    cards/xxx.png
    tokens/xxx.png
    audio/xxx.mp3
```

注意：`client/Assets/Scripts/TutorialPlayer.cs`（旧原型）与本播放器会各自搭景，二选一运行。新教程走 `TutorialDirector`，旧原型建议删除或禁用。

## 从 flow.json 自动生成草稿

`scripts/flow_to_tutorial.py` 会把 flow.json 里已经结构化好的语义直接翻译成 tutorial.json：

- `transfer` / `random_draw` / `top_draw` / `play` → `move`
- `shuffle` → `shuffle`
- `state_change` → `highlight`
- 没有对应动画语义的节点 → `wait`（待人工补动画）

```bash
python scripts/flow_to_tutorial.py --game splendor
python scripts/flow_to_tutorial.py --game civolution --stdout > /tmp/civolution.tutorial.json
```

产物是「结构正确的草稿」：
- 章节来自 flow 的叶子 phase/round；
- 时间轴事件来自 flow 的叶子动作节点，顺序与 flow 一致；
- slot 坐标是自动网格占位，需要你在版图扫描图上校准；
- sprite 路径是 `media/auto/{id}.png` 占位，需要替换为实际扫描/拍摄图。

这样 LLM 的职责从「创作动画」降为「校准坐标 + 替换素材 + 补充 wait 节点的动画」。

## 工作流

1. 先跑 `flow_to_tutorial.py` 生成草稿；
2. 替换 `media/auto/*` 为真实素材，校准 slot 坐标；
3. 跑 `scripts/validate_tutorial.py --game xxx`，错误全部清零；
4. 再进 Unity 看效果，之后只调视觉参数，不再改数据结构。

## 下一阶段路线

讲规动画从「章节 + 时间轴」扩展到「口播稿层 + 版本（quick/full）+ 编译期起始画面」的方案，见：

`tutorial/下一阶段工作指导.md`

## 时间轴口播稿（LRC-like）

口播稿以类似歌词的 LRC 格式存放，例如 `games/splendor/tutorial/full.lrc`。播放器/编译器不再直接读 Markdown，而是解析该格式后再生成运行时数据。

格式约定：

- 元数据行：`[ti:标题]`、`[game:splendor]`、`[track:full|quick]`、`[timing:estimated|tts]`、`[version:...]`、`[length:mm:ss.xx]`
- 分组行：`[group:3.1 从宝石供应堆拿取宝石]`，只用于导航，没有时间。
- 台词行：`[mm:ss.xx][id:...]台词`，时间表示该播放单元的开始时间。
- 可选引用：`[ref:<concept>|flow:node|rulebook:xxx]`，多个引用用 `|` 分隔。
- 一个 `id` 就是一个播放单元；时间结束以下一条时间或 `[length:...]` 为界。
- `[timing:estimated]` 表示当前时间是 TTS 前的估算；TTS 冻结后应改成 `[timing:tts]` 并重写时间。
- 校验/解析：`python scripts/validate_timed_script.py --file games/splendor/tutorial/full.lrc`；加 `--json` 可输出结构化结果供后续编译器使用。
