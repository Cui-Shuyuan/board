# teaching.json 数据格式设计（v0）

> 目标：让「分块教学 + 动画」完全数据驱动，替换现有硬编码的 `TutorialPlayer.cs` 演示。
> 播放器只负责根据本格式逐块播放字幕 + 驱动 sprite 补间动画；改稿子/动画 = 改数据，不改代码。
> 遵循项目「程序确定性，LLM 只做语言理解」的哲学。

## 顶层结构

```json
{
  "meta": {
    "game": "splendor",
    "title": { "zh": "璀璨宝石 · 设置", "en": "Splendor · Setup" },
    "version": "0.1.0",
    "camera": { "orthographicSize": 3.6, "angle": 50, "position": [0, 7.66, -6.43] }
  },
  "chunks": [ ... ]
}
```

## chunk（教学分块）—— 可打断的最小单位

每一块 = 一句/一小段旁白 + 一段动画。客人打断后从**当前 chunk id** 重播。

```json
{
  "id": "setup_deal_market",
  "title": { "zh": "发市场牌" },
  "text": { "zh": "每一级翻开四张发展卡，摆在桌上。", "en": "Reveal four cards of each level to the market." },
  "shots": [ ... ]       // 动画时间轴（顺序播放）
}
```

字段说明：
- `id`: 全局唯一，供打断/重播/上下文定位
- `text`: 字幕（未来 TTS 读这段）
- `shots`: 该块的动画序列，**顺序**播放，每 shot 完成后进入下一个
- `title`: 可选，供目录/进度显示

## shot（动画指令）

```json
{
  "type": "move",                    // 见下方指令类型
  "target": "card_market_1_1",       // 场景内的 GameObject 名（slot 命名约定）
  "from": [0, 0, 0],                 // 可选，缺省=当前变换
  "to": [0, 0, 0],                   // move/flip/缩放必需；rotate 用 angle
  "duration": 1.2,                   // 秒
  "easing": "easeOutCubic",          // easeIn/Out/InOut + linear
  "delay": 0,                        // 可选，该 shot 前的等待
  "hold": 0,                         // 可选，该 shot 完成后的停留
  "angle": [0, 180, 0],              // rotate 用（欧拉角）
  "items": [ ... ],                  // 并行组用：同组 shots 同时播，播完一起结束
  "hidden": false                    // appear(从隐藏到显示) 用
}
```

### 指令类型（type）
| type | 含义 | 关键字段 |
|---|---|---|
| `move` | 平移 | from, to, duration, easing |
| `rotate` | 旋转 | angle(欧拉), duration, easing |
| `scale` | 缩放 | to, duration, easing |
| `flip` | 翻面（绕某轴 180°，常用于卡牌翻开） | angle, duration, easing |
| `appear` | 出现（从透明/隐藏到显示，配合 scale 或 fade） | duration, easing |
| `unfold` | 折叠物展开（铰链子节点逐个旋转） | items 子对象 angle, duration |
| `tell` | 纯旁白/停顿（无动画，只等 duration） | duration |
| `group` | 并行组（items 内子 shot 同时播放） | items, duration | `duration` 为整组时长上限 |

### easing 可选值
`linear`, `easeInQuad`, `easeOutQuad`, `easeInOutQuad`, `easeInCubic`, `easeOutCubic`, `easeInOutCubic`, `easeInSine`, `easeOutSine`, `easeInOutSine`

## 打断 / 重播 / 提供给 LLM 的上下文

播放器维护运行状态：
```json
{
  "state": "playing",
  "current_chunk": "setup_deal_market",
  "chunk_index": 3
}
```

- 打断时：立即停当前 shot，`current_chunk` 停在哪块就是哪块
- 重播：`PlayChunk(current_chunk)` 从该块首个 shot 重新播
- 给 LLM：`{"current_chunk_id": "...", "current_title": "...", "current_text": "...", "full_script": "<全部 chunk 的 title+text>"}`

## 与 Splendor 规则数据对齐

本格式的 `shots` 不硬编概念 id（那是 rules 层的职责），只引用**场景槽位名**（由 Unity 侧 slot 标注约定）。规则语义（如 distribute_gems 按人数）已由 `games/splendor/flow.json` 表达，教学脚本按规则书口吻写旁白。

## 相关
- 播放器实现见 `Assets/Scripts/TeachingPlayer.cs`
- 出图自检见 `Assets/Scripts/Editor/BatchRender.cs`
- 示例数据见 `Assets/Resources/teaching_splendor_setup.json`
