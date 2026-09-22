---
name: animation-pipeline
description: 动画制作流水线——Image-to-3D vs LLM vs 手动标注的边界（3D 车道备用参考）
metadata:
  type: project
---

# 动画制作流水线

## 2026-08-15 更新：本文为 3D 车道备用参考

视觉层已转向 **2D/2.5D sprite 伪 3D**（详见 [[tutorial-module]]），Image-to-3D 与剪影挤出均退为备胎。「slot 空物体手动摆放」方案在 2D 下同样适用；其余内容（模型=皮肤、坐标=大脑、各环节分工）作为备用知识保留。

## 2026-08-15 讨论澄清的三个认知

### 1. Image-to-3D ≠ LLM

- **Image-to-3D 专用模型**（Meshy/Tripo/Rodin/Hunyuan3D）：从照片生成 3D mesh + 纹理，是视觉生成模型
- **多模态 LLM**（GPT-4V/Claude Vision/Gemini）：能看图、能描述，但**不能输出 3D 模型文件**
- 两者是不同技术栈，不能互相替代
- Image-to-3D 不能识别语义（不知道哪里是 slot 1）

### 2. 动画不需要多模态 LLM

动画 = 变换参数（position/rotation/scale + 时间），是纯数值/逻辑。LLM 的角色是**生成代码和数据**，不在运行时参与播放。纯文本 LLM 完全够用。

### 3. 模型是「皮肤」，坐标是「大脑」

- 3D 模型只提供视觉外观
- slot 的逻辑位置由数据/代码定义，不是从模型里读出来的
- Unity 在定义好的坐标上画格子，模型只是底下的装饰

## slot 标注方案：Unity 空物体手动摆放

用户在 Unity 编辑器中创建空 GameObject（`Slot_0` ~ `Slot_N`），拖到版图模型对应位置。动画代码通过 `GameObject.Find("Slot_5")` 引用。

**这是最适合非程序员的方案**——所见即所得、零编程门槛。

详见 [[tutorial-module]] §slot 标注方案。

## 各环节负责方

| 环节 | 工具 | 说明 |
|---|---|---|
| 动画代码 | 文本 LLM | 写 C# 协程/插值 |
| 动画数据 | 用户口述 → LLM | 转 tutorial.json |
| 3D 模型 | Meshy（立体件）/ 剪影挤出（扁平件） | Image-to-3D，非 LLM |
| slot 标注 | Unity 编辑器手动摆放 | 用户操作 |
| 动画执行 | Unity 运行时 | 纯程序 |

## 相关记忆

- [[tutorial-module]] — 第五阶段技术选型与完整规划
- [[project-overview]] — 开发阶段

**Why:** 这次讨论澄清了「LLM 能做什么」和「不能做什么」的边界，避免后续混淆 Image-to-3D 和 LLM 的职责。
**How to apply:** 动画制作时，模型生成走 Image-to-3D 服务，代码和数据走文本 LLM，slot 位置由用户在 Unity 中手动标注。
