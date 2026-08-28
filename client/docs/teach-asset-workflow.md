# 桌游教学动画：素材到动画工作流协议 v0

> 目标：建立一套可复用的工作流——用户提供桌游配件照片，AI 完成「识别 + 处理 + 做进动画」，
> 产出**静态可复用的数据资产**（不靠 LLM 运行时实时生成）。适用于任意桌游。
>
> 遵循项目哲学：程序确定性（LLM 只做语言理解），动画 = 静态数据资产。

## 核心思路

图片识别/处理是一次性的**编辑时工序**；动画播放是**运行时工序**。两者分离：
- 编辑时（做动画）：拍照片 → AI 识别 → 抠图/透视处理 → 转成 sprite + slot 数据 → 写 tutorial.json
- 运行时（客人看）：Unity 读 tutorial.json + sprite 资源，纯程序播放，零 LLM

## 一、你的输入协议：给 AI 什么图片

每种配件一张（或多张）有明确约定的照片。**协议 = 拍摄规范 + 命名规范**，这样 AI 能确定性地识别。

### 拍摄规范
| 配件 | 拍摄要求 |
|---|---|
| **版图/玩家板** | 正上方俯拍整张，一张即可；光照均匀、无反光（交叉偏振）。用于透视校正成平面 sprite |
| **卡牌** | 正上方俯拍单张正面，**卡放纯色（绿/蓝）背景**便于抠图；要看出卡面细节 |
| **token/筹码** | **正上方俯拍俯视**，token 平放对比色背景（见 extract_token_silhouette.py 既有规范） |
| **贵族/板块** | 正上方俯拍、平放对比色背景 |
| **折叠板** | 额外补拍：展开态正上方 + 折叠态侧面（识别铰链折痕） |

### 命名规范（AI 识别 + slot 定位依赖）
`games/{game}/media/capture/{name}/`
- `{name}` = 配件语义名（与概念 id 对应，如 `development_card_level_1`、`noble`）
- 命名用**概念 id**（来自 `games/{game}/concepts.json`），保证跟规则模型对齐、跨游戏可复用

## 二、AI 处理工序（一次性转成数据资产）

复用你已有的脚本：`extract_token_silhouette.py`（轮廓抠图）等。流程：

```
照片 → 抠图/透视校正 → 输出到 games/{game}/media/capture/{name}/
         ├── texture_front.png   (RGBA 透明背景贴图)
         ├── contour.json        (归一化轮廓 + 真实尺寸)
         └── mask.png            (二值掩码，备用)
```

**AI 在工序里的角色**：识别（这是哪张卡/哪种 token）、做透视校正、生成/标注、把结果写进数据文件。**不做**运行时播放。

## 三、让 Unity 动画消费这些素材：slot 协议

你的项目已有约定（记忆里 tutorial-module）：**slot 用 Unity 空物体手动摆放**，动画代码用 `GameObject.Find("Slot_5")` 引用。

我补充：**槽位 → 素材 → 动画 三段映射**，全部数据驱动：

```
games/{game}/tutorial.json          ← 动画分块(旁白 + shots 补间)
    └── shot.target 引用 "slot 名"    ← 动画指令指向哪个槽位

games/{game}/sprites.json           ← 素材映射：slot 名 → texture_front.png
    ├── development_card_level_1  → media/capture/development_card_level_1/texture_front.png
    └── gem_sapphire              → media/capture/gem_sapphire/texture_front.png

games/{game}/slots.json            ← 槽位坐标：slot 名 → 世界坐标(50°俯角下)
```

**Unity 运行时**：读 sprites.json 加载贴图（替换 GameSpriteFactory 占位色块），读 slots.json 摆放对象，读 tutorial.json 播放动画。**三个文件全是静态数据，改动画=改数据，零代码。**

## 四、跨游戏复用

- 协议本身**与游戏无关**（都是 图片 → sprite+slot → tutorial.json 的模式）
- 换游戏 = 换一套 `games/{game}/media/capture/` + 三个 json；Unity 代码零改动
- 卡牌/贵族等**通用配件类型**的处理规则（抠图、透视）可沉淀为**模板**，新游戏复用

## 五、AI 提供给你的交付方式（我现在做的部分）

AI（我）向你提供这套工作流的**执行能力**：
1. `scripts/ingest_teach_asset.py` —— 吃原始照片，跑抠图/透视，输出那三个文件
2. 根据 concepts 自动生成 `sprites.json` / `slots.json` 骨架
3. 帮你把占位 sprite（GameSpriteFactory）替换为「读 sprites.json 加载真图」

## 六、你要做的（最省事）

1. **按拍摄规范拍照**（每配件一张俯拍，放对比色背景）
2. 把照片放到 `games/{game}/media/raw/{name}.jpg`（或告诉我路径）
3. 之后**全部我来**：识别 → 抠图 → 生成数据 → 替换动画素材 → 你重开 Unity 看

## 关键约定（定稿 2026-08-29）
- [x] 配件命名用**概念 id**（对齐规则模型、跨游戏一致）——如 `development_card_level_1`
- [x] 槽位坐标：**我（AI）按 50° 俯角算好，写进 slots.json，全自动**——用户不进编辑器拖拽
- [x] 拍照：**一配件一图**（每个配件单独俯拍、放对比色背景），识别最准
