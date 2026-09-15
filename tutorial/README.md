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

## cue 内动画：状态 / zone 模型（2026-09-15 定稿）

动画 = **维护一组组件的状态**。组件的状态就是它「在哪个 zone、以什么姿态」；
世界坐标由 zone 的布局规则推导，**cue 数据里不写坐标**。

数据分两层：

```text
games/{game}/tutorial/anim/_stage/{game}.table.json   牌桌事实：zone 位置、模板、开局摆放
games/{game}/tutorial/anim/{track}/{cue_id}.json      这一条 cue 对牌桌做了什么
```

- **stage**：`zones`（每个 zone 的 `center` 与 `layout`）、`templates`（外观：shape / palette / world_size）、
  `anchors`（区域底板等固定装饰，`zones` 字段声明它代表哪些 zone）、`initial`（开局哪些组件放在哪些 zone）。
- **cue**：只有 `events`，外加可选的 `start`（本条 cue 的入口状态）。移动事件只写两端 zone：

```json
{ "at": 3.30, "dur": 0.55, "action": "move", "target": "gem#1", "zone": "player_holding" }
{ "at": 3.30, "dur": 0.55, "action": "move", "from": "gem_supply_diamond", "take": 3, "zone": "player_holding" }
```

两种写法等价：前者指定具体某一件（运行时实例 id = `{template}#{序号}`），
后者表示「从源 zone 里搬最前面的 N 件」。

关键性质：

- **同一个原语覆盖设置阶段与游戏阶段**。玩家「从供应堆拿到持有区」和设置阶段
  「从镜头外飞进供应堆」都是一次 zone → zone 的移动，只是源 zone 不同
  （`offstage` 是镜头外的入场通道）。
- **搬走会触发顺位收拢**。同一 zone 里排在后面的组件自动前移，所以「拿走一枚宝石」
  看到的是堆真的少了一枚，而不是留一个空位。
- **入口状态、重播与顺序播放**。顺序播放时一条 cue 接着上一条的终态；重播当前 cue
  恢复到这条 cue 的入口状态。将来编译器可以离线复算每个 cue 的入口状态写进 runtime，
  用于任意跳转。

校验：

```bash
python scripts/validate_cue_anim.py --game splendor --track full
python scripts/validate_cue_anim.py --game splendor --track full --cue action.take.different.001
```

进 Unity 之前还会跑一次 C# 编译检查（用生成的 Unity API stub，不需要 Unity）：

```bash
python scripts/check_unity_scripts.py
```

## 时间轴口播稿（LRC-like）

口播稿以类似歌词的 LRC 格式存放，例如 `games/splendor/tutorial/full.lrc`。播放器/编译器不再直接读 Markdown，而是解析该格式后再生成运行时数据。

长 cue 可以用 `scripts/split_lrc_long_cues.py` 按标点拆分：

```bash
python scripts/split_lrc_long_cues.py   --input games/splendor/tutorial/full.lrc   --output games/splendor/tutorial/full.short.lrc   --max-chars 40
```

Splendor full 已按此规则拆成 109 个 cue，避免单条 15 秒以上导致打断后重听过长。

格式约定：

- 元数据行：`[ti:标题]`、`[game:splendor]`、`[track:full|quick]`、`[timing:estimated|tts]`、`[version:...]`、`[length:mm:ss.xx]`
- 分组行：`[group:3.1 从宝石供应堆拿取宝石]`，只用于导航，没有时间。
- 台词行：`[mm:ss.xx][id:...]台词`，时间表示该播放单元的开始时间。
- 可选引用：`[ref:<concept>|flow:node|rulebook:xxx]`，多个引用用 `|` 分隔。
- 一个 `id` 就是一个播放单元；时间结束以下一条时间或 `[length:...]` 为界。
- `[timing:estimated]` 表示当前时间是 TTS 前的估算；TTS 冻结后应改成 `[timing:tts]` 并重写时间。
- 校验/解析：`python scripts/validate_timed_script.py --file games/splendor/tutorial/full.lrc`；加 `--json` 可输出结构化结果供后续编译器使用。

## 口播稿转音频（豆包语音）

`scripts/tts_doubao.py` 使用火山引擎豆包语音合成 2.0 的 WebSocket 双向流式接口，把 LRC-like 口播稿逐 cue 合成为音频：

```bash
pip install -r scripts/requirements-tts.txt

# 只预览
python scripts/tts_doubao.py --input games/splendor/tutorial/full.lrc --dry-run

# 先试听前 3 条
python scripts/tts_doubao.py --input games/splendor/tutorial/full.lrc --limit 3

# 全量生成，并回写 full.tts.lrc
python scripts/tts_doubao.py --input games/splendor/tutorial/full.lrc --write-lrc
```

- 凭证从仓库根目录 `.env` 读取：`VOLCENGINE_API_KEY`
- 输出：`games/{game}/media/tts/{track}/{cue_id}.mp3`、`{cue_id}.subtitle.json`、`tts_manifest.json`
- `--voice` 指定音色，默认 `zh_female_vv_uranus_bigtts`
- `--limit N` 用于小样试听；去掉后全量生成
- `--write-lrc` 生成 `full.tts.lrc`，把 `[timing:estimated]` 改写为真实时长

## 运行时 cue 数据

TTS 产物通过编译器合并为运行时播放数据：

```bash
python scripts/build_tutorial_runtime.py --game splendor --track full --force
```

输出：

```text
games/{game}/tutorial/{track}.runtime.json
```

内容：

- cue 顺序、音频相对路径、真实时长
- 字级字幕时间戳
- 导航用 `group_path`
- `refs`
- `animation: null`（动画后续按 cue id 挂独立数据）

## Unity 纯音频播放器

`client/Assets/Scripts/Tutorial/TutorialCuePlayer.cs` 是纯音频播放器 v0：

- 读取 `{track}.runtime.json`
- 播放 mp3、显示字幕
- 上一段 / 下一段 / 跳转 / 暂停 / 重播当前 cue
- 暴露 `CurrentCueId`、`CurrentCueText`、`CurrentCueGroupPath`，供后续打断问答使用

它默认启用后会关闭旧 `TutorialDirector` 的自动动画搭景；需要回到旧动画原型时，把 `TutorialCuePlayer.DisableLegacyBootstrap` 设为 `false`，或手动把 `TutorialDirector` 挂到场景。

## 分层口播稿编辑流程（2026-09-13）

为了避免频繁手改 LRC 时间和 TTS 音频，口播稿现在以 source JSON 为编辑源：

```text
games/{game}/tutorial/script.{track}.json
```

结构：

```text
group_path -> cue -> beat
```

- `cue` 是 TTS 单元：一条 cue 一个音频文件。
- `beat` 是文本最小单位：拆分 / 合并 / 动画 shot 都按 beat 分组。
- `cue.pause_after` 是播完本 cue 后给动画留白的秒数。

常用命令：

```bash
# source JSON -> estimated full.lrc
python scripts/tutorial_script_tool.py build --game splendor --track full

# 拆分 cue（--at 后的文本成为新 cue）
python scripts/tutorial_script_tool.py split --game splendor --track full \
  --cue action.take.different.001 --at "拿取"

# 合并相邻 cue
python scripts/tutorial_script_tool.py merge --game splendor --track full \
  --cues bg.intro.002.1,bg.intro.002.2

# 给 cue 尾部留白
python scripts/tutorial_script_tool.py set-pause --game splendor --track full \
  --cue action.take.different.001 --seconds 1.2
```

完整重生成（LRC → TTS → runtime）：

```bash
python scripts/rebuild_tutorial.py --game splendor --track full
```

只验证 source / estimated LRC、不重跑 TTS：

```bash
python scripts/rebuild_tutorial.py --game splendor --track full --skip-tts
```

TTS 脚本已支持：

- `--force`：忽略已有音频，全量重生成。
- `--prune`：删除 source LRC 中已不存在的旧音频和字幕。
