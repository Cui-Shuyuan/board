---
name: tutorial-module
description: 第五阶段讲规模块——技术选型定稿（Unity 原生安卓 + 数据驱动 2D/2.5D sprite 动画，50° 固定俯角）；Unity 6 环境已就绪（全落 D 盘）；client 骨架与首个原型已跑通，观感迭代改用多模态模型发截图；2026-09-13 下一阶段转向讲规动画自动制作
metadata:
  type: project
---

# 讲规模块（第五阶段 Tutorial Tree）

## 需求（2026-08-14 用户口述）

播放被切成小段的**静态音频 + 动画**，让客人自助学会规则：

- 设备：店内提供的平板，白名单连接内网 wifi，与店内电脑通信。**内网不向客人开放**（安全 + 兼容性考虑）
- 播放器控制：手动下一条 / 自动播放 / 拖动进度条 / 上下滑动选章节跳转
- **打断闭环**：客人随时打断提问 → 问答模块登场，prompt 带当前章节信息 → 回答后可选继续提问或继续学习；**继续学习 = 从当前章节重播**（章节切得细，重播优于续播——用户例：「拿取三 m[打断][回答]ei 颜色不同的宝石」体验很差）
- 用户对动画制作和 Unity 都零基础

## 技术选型（2026-08-15 讨论定稿）

### 客户端：Unity 原生安卓 App

- **安卓平板**（非 H5）。测试机：**一加 Ace 2（主测试机，骁龙 8+ Gen 1）+ 红米 K30 Pro（兼容性下限）**
- H5 被否的关键理由：**浏览器麦克风要求 HTTPS 安全上下文**——内网 HTTP 的 H5 做不了按住说话（未来 PTT 核心交互）；原生 App 无此限制
- 内容仍走数据驱动：tutorial.json + 音频 + 动画资源从店内电脑 HTTP 拉取，App 壳基本不更新
- iPad 不在考虑范围（安卓侧载无痛）

### 视觉层：2D/2.5D sprite 伪 3D ★（2026-08-15 晚，用户讨论后转向）

原定「程序化几何体 + 剪影挤出 + 贴图薄片」3D 方案改为 **2D/2.5D sprite 起步**，理由：

- **固定机位下 3D 自由视角无价值**——教学动画客人只能看、不能拖动视角，3D 的核心优势（自由视角）本来就放弃了
- **Civolution 组件 95% 是扁平件**——token/卡牌/板块俯视就是一张图，3D 只多 2 毫米厚度，固定俯视下几乎不可见
- **零基础用户 + 文字迭代 2D 友好**——「边缘有白边」说得清，「阴影软 10%」说不清；2D 没有光照/材质/相机四组观感参数要调
- **资产管线大幅简化**——剪影挤出（轮廓→挤出 Mesh）退化为**剪影抠图**（轮廓→alpha mask PNG→直接当 Sprite）；`extract_token_silhouette.py` 的输出直接可用
- **AI 3D 车道正式退休**（备而不用）；程序化几何件（六棱柱等）同样退为备胎
- **保留「局部按需升 3D」口子**——折叠纸板展开等场景 2.5D 观感打折，Unity 2D/3D 混合免费，个别章节可局部加 3D

**相机：50° 俯角固定机位 ★（用户定）**——与客人坐在桌边的视角一致，代入感最好；越俯越清晰、越平越代入，50° 在甜区（Watch It Played 与主流桌游数字版同款机位）。正交相机 + 微倾 billboard 保证可读性。

**风格：实物照片 sprite，不是 Q 版插画**——教学目的是「客人学完认得实物」，动画里长什么样桌上就长什么样；Q 版插画资产贵、风格难统一、与实物脱节。照片处理流水线：柔和漫射光（反光用交叉偏振根治：偏振膜贴灯 + 镜头 CPL，黑镜测试校准）→ 四点透视校正（OpenCV `getPerspectiveTransform`）→ 输出 PNG。

**可变形件的处理（2026-08-15 用户指出）**：对折纸板 = 已知几何，不走 Image-to-3D——两块矩形平面 + 折痕铰链（pivot 空物体 + localRotation 插值），折痕线天然是 UV 接缝，照片瑕疵全藏在铰链处。

### Image-to-3D 备用知识（仅真雕刻件车道，2026-08-15 调研）

仅真 3D 雕刻件（Scythe 机甲类）可能需要；Civolution 无此类组件：

- **提示词可选，图片必须**——Tripo 会静默忽略图片+文本同时输入；Meshy image-to-3D API 请求体只有 `image_url` 字段。提示词只在「文本转 3D」模式必填
- **多视角照片是关键**（正/侧/背，1-4 张）——单图背面是 AI 猜的；实物必须自拍背面，虚构件可让 AI 补视角
- 2026-04 实测对比：Tripo v3.1 最快/拓扑最干净/风格化最佳；Rodin Gen-2.5 硬表面细节最好（需 retopo）；Meshy 6 纹理最好最均衡。单次 API 约 $0.18-0.25，约 1/10 直接可用，需多轮重试
- 本地 Hunyuan3D：shape-only ~6GB、完整纹理 24.5GB+；RTX 4080 Super 16GB 只够 shape-only + 自贴照片纹理；2.5 的本地权重未正式发布（本地实际是 2.1）
- 批量管线可挂 [trident-mcp](https://github.com/mordor-forge/trident-mcp)（Tripo/Meshy/Rodin 多供应商 MCP server）

### Unity 环境与存储（2026-08-15 全部落地 D 盘）

- **unity.com 对中国 IP 地理引流（实测）**：`unity.com/download` 302→`unity.cn/releases`，连国际 CDN 直链都 307→`unitychina.cn`——国内直下国际版需代理；但**日常使用不需要代理**（实测 CDN/许可证/UPM 三端点直连 HTTP 200），代理只在开官网下载页时用
- 最终装成国际版 **Unity 6（6000.5.8f1）**，全落 D 盘：Hub 在 `D:\Unity Hub`、编辑器 `D:\Unity\Hub\Editor`、下载缓存 `D:\Unity Hub\downloads`（Hub 检测到 C 盘满自动选 D）
- **环境变量**：`UPM_NPM_CACHE_PATH=D:\Unity\Cache\packages`（包缓存，默认写 C 的 AppData 会撑爆 C 盘）、`TMP/TEMP=D:\Temp`（装安卓模块等解压用）；环境变量需重启 Hub 才被继承
- C 盘 99GB 总量只剩 1GB，是其他大户（与 Unity 无关，待清理）；Unity 已全部迁离 C
- **国内搜索教训**：中文搜「unity 下载」必得 unity.cn 团结系（团结引擎是正牌 Unity 中国版，内核同源；「团结 AI/Codely」是它推的 AI 助手产品，我们用不上）；国际版安装包走代理拿

## client 项目骨架（2026-08-15）

- **位置**：`D:\workspace\board\client`（在仓库内，git 管理）。git 约定：**代码/JSON/场景/成品素材入库；原始照片等大素材放仓库外**（git 永久保存每个版本，二进制会撑爆仓库）。动画架构无视频文件——动画 = tutorial.json（文本）+ sprite 图 + 音频
- **打开方式**：Hub → Add project from disk（**选择窗口选中「当前所在文件夹」= 窗口顶部路径栏显示的，不是列表里高亮的那个**）；或双击 `client\打开项目.bat` 直接启动编辑器
- **骨架文件**（全部纯文本，不碰编辑器界面）：
  - `Assets/Scripts/TutorialPlayer.cs`——`RuntimeInitializeOnLoadMethod` 运行时搭景：50° 正交相机、棋盘/三圆片/接触阴影、字幕 UI（微软雅黑 + 黑描边）、演示动画「拿取三颗不同颜色宝石」（easeOutCubic 缓动），空格/R 重播
  - `Assets/Resources/Sprites/*.png.bytes`——占位素材（`scripts/generate_placeholder_sprites.py` 生成）；`.bytes` 后缀绕开纹理导入设置，运行时 `Sprite.Create` 构造
  - `client/.gitignore`——排除 Library/Temp/UserSettings 等生成目录
- 模板为 Universal 3D（URP）——所有视觉用 SpriteRenderer 渲染，URP 下无碍
- **首个原型已跑通**：按 Play 出动画（用户已看）；**观感不理想，待调**。迭代方式升级：**用户换多模态模型，直接发截图反馈**（此前纯文字描述视觉问题是瓶颈）

## 动画制作分工

### 核心认知：外观 ≠ 逻辑

**外观（sprite 图片）与逻辑（slot 坐标）独立**——动画代码引用 slot 的 GameObject 名称，不引用像素坐标；Image-to-3D 服务（如用）只输出外观，不能识别语义。

| 环节 | 负责方 | 说明 |
|---|---|---|
| 动画代码（怎么移动/翻面/洗混） | LLM（文本） | 纯编程，C# 协程/插值 |
| 动画数据（哪个素材、到哪、多久） | 用户口述 → LLM 转 tutorial.json | 用户描述效果，LLM 写 JSON |
| sprite 素材（照片抠图） | 用户拍照 → 脚本处理 | 剪影抠图/透视校正，非 AI 3D |
| slot 位置标注 | 用户在 Unity 编辑器中手动摆放 | 空 GameObject，所见即所得（2D 下同样适用） |
| 动画执行 | Unity 运行时 | 纯程序，零 LLM 调用 |

### slot 标注方案：Unity 空物体手动摆放

用户在 Unity 编辑器中创建空 GameObject（`Slot_0` ~ `Slot_N`），拖到版图上对应位置，挂为版图子物体。动画代码 `GameObject.Find("Slot_5")` 引用。**用户摆在哪就是哪，不需要坐标数据文件。** 命名必须统一（用户摆的名称 = LLM 写代码引用的名称）。

### 动画的本质

动画 = 变换动画（position/rotation/scale），不是骨骼/逐帧动画：marker 移动 `Vector3.Lerp`、卡牌翻面绕 Y 轴 180°、洗混多张牌交错、拿起放大。**全部是纯代码，几十行 C# 协程搞定。**

### 产出是数据资产

动画做好后是静态数据（tutorial.json + 素材），客人学习时只做播放，不实时生成。来回返工无成本。

## 下一阶段方向（2026-09-13 更新）

讲规模块下一阶段重心转为**讲规动画自动制作**：

- 口播稿是主，动画是次；先定稿口播，再 TTS，音频冻结后动画只在其时长内制作。
- 播放单元按 1～3 句口播切；规则小节只作 group/导航层。
- quick / full 共享组件、资产和动作行为，但口播稿各写一份；full 先做，quick 为店里默认首讲版本。
- 客人可任意跳转；运行端只维护当前播放单元，跳转所需「起始画面」由编译器离线生成。
- 动画继续只用 8 个原语，LLM 只填参数，不写新协程；不做 authoring 界面。
- 试点《璀璨宝石》，标杆稿为 `doc/splendor/口播稿.md`（full 版）；已拆出 `games/splendor/tutorial/full.lrc`（LRC-like，59 cues）。

详见 `tutorial/下一阶段工作指导.md` 与 [[tutorial-production-pipeline]]。

## 待定事项

- 音频来源：TTS 生成（快、批量、机械）vs 真人录音（贵、慢、质感好）
- 最小原型范围：一个章节跑通「播放→打断→问答→重播」全链路
- **首个原型观感迭代（进行中）**：用户换多模态模型后按截图反馈调参（相机高度、圆片大小、配色、阴影、字幕样式等）
- **剪影 PoC 待拍俯拍照**：`scripts/extract_token_silhouette.py` 已写好（照片→contour.json + texture_front.png + mask.png + debug.jpg 核验图，输出 `games/{game}/media/capture/{name}/`）；首个试件 = Civolution 部落 token
- **slot 标注的具体命名规则**：待用户进编辑器实操时确认（`Slot_0` / `slot_0` / `progress_0` 等）
- **安卓模块未装**（Windows 预览优先；Android SDK/NDK 模块以后再加）
- 版图/组件实物照片全部待拍（token、版图、卡牌、板块）

## 相关记忆

- [[project-overview]] — 开发阶段规划（第五阶段）
- [[interaction-model]] / [[runtime-architecture]] — 问答模块现状与打断衔接点
- [[animation-pipeline]] — 3D 车道备用参考（Image-to-3D 分工边界）
- [[user-preferences]] — 数据驱动哲学、程序确定性

**Why:** 第五阶段经历了 H5→原生（麦克风安全上下文）与 3D→2.5D（固定机位 + 扁平件 + 零基础迭代）两次大转向，新会话需要知道定稿结论、环境现状与 client 骨架。
**How to apply:** 讲规模块的任何实现讨论以「Unity 原生 + 2.5D sprite + 50° 固定俯角」为前提；观感迭代靠用户发截图（多模态模型）。
