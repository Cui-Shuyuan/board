# 教学动画素材数据格式（sprites.json / slots.json）

> 配套协议见 teach-asset-workflow.md。这两个文件把「外部处理好的素材」映射到 Unity 动画，
> 替换掉运行时的占位色块（GameSpriteFactory）。全部静态数据，改素材 = 改 json，零代码。

## sprites.json —— 素材映射：slot 名 → 贴图文件

放在 `Assets/Resources/teaching/{game}/sprites.json`

```json
{
  "game": "splendor",
  "sprites": [
    { "slot": "development_card_level_1", "png": "splendor/development_card_level_1" },
    { "slot": "noble",                    "png": "splendor/noble" },
    { "slot": "gem_sapphire",             "png": "splendor/gem_sapphire" },
    { "slot": "board",                    "png": "splendor/board" }
  ]
}
```

- `slot`：动画里引用的对象名（= 概念 id，与 tutorial.json 的 shot.target 一致）
- `png`：贴图资源路径（相对 `Assets/Resources/`，去掉扩展名的 `.png`；Unity 从 Resources 加载）

## slots.json —— 槽位坐标：slot 名 → 世界坐标（50° 俯角下）

放在 `Assets/Resources/teaching/{game}/slots.json`

```json
{
  "game": "splendor",
  "camera": { "angle": 50, "orthographicSize": 3.6, "position": [0, 7.66, -6.43] },
  "slots": [
    { "slot": "development_card_level_1", "at": [-1.20, 0.03, 0.55], "scale": 0.42, "order": 10 },
    { "slot": "noble",                    "at": [-2.10, 0.02, 2.55], "scale": 0.50, "order": 20 },
    { "slot": "gem_sapphire",             "at": [-1.68, 0.02, -2.55], "scale": 0.34, "order": 12 }
  ]
}
```

- `slot`：概念 id
- `at`：世界坐标 [x, y, z]（50° 俯角下由 AI 计算，全自动，用户不进编辑器拖）
- `scale`：sprite 世界缩放（按 100 PPU 换算）
- `order`：sortingOrder（层级）

## Unity 加载逻辑

`TeachingPlayer.BuildScene` 改为：
1. 读 `slots.json` → 拿每个 slot 的坐标/scale/order
2. 读 `sprites.json` → 拿 slot 对应的贴图
3. `GameSpriteFactory` 退化为**兜底**：sprites.json 里没有的 slot，仍用程序化占位色块
4. 优先用真实贴图，缺素材才用占位

## 文件位置约定
- 数据：`Assets/Resources/teaching/{game}/sprites.json`、`slots.json`、`tutorial.json`
- 贴图：`games/{game}/media/capture/{name}/texture_front.png` → 由脚本拷/转为 Resources 下 PNG
