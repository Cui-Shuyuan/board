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

## 工作流

1. 先写 `tutorial.json`（或让 LLM 生成）。
2. 跑 `scripts/validate_tutorial.py --game xxx`，错误全部清零。
3. 再进 Unity 看效果，之后只调视觉参数，不再改数据结构。
