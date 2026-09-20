# 讲规动画脚本（full.json）—— **手写的静态资产**

## 谁写、怎么定位（用户 2026-09-20 定）

用户原话：「这个脚本就应该完全由你自己写，**不应该让程序去生成**（过账除外），除非是那种
非常确定的、没有任何争议的问题，可以让程序去做。因为教学动画，它是个**一次做好永久使用**的东西，
不像问答功能每次都得对。你就算写错了，我自己验收的时候发现问题，让你改就好了，
改完之后，动画直接成为了一个**静态的固定资产**，只用于播放。」

所以：

- **`full.json` 是唯一的资产，由人（我）手写**：story/note/timing、契约 enter/exit、事件、
  取景（`camera` 写"要入镜的那几个 zone" + `camera_fill` 填充率）——全部是判断，不是推导。
- **程序只做"确定无疑"的事**：
  - `scripts/validate_cue_anim.py` —— 数据/本体/取景链/字段归属层的体检
  - `scripts/validate_anim_rules.py` —— **过账**：把事件重放成状态，逐步验规则（每色在场 4 颗、
    手上限 10、保留上限 3、买牌「价格−折扣==实付」…）
  - `scripts/check_unity_scripts.py` —— 编译
  - `scripts/check_cue_script.py --all` + `scripts/dump_states.sh` —— **对账**（契约 vs 引擎采样）
  - `scripts/qa_anim_ask.py` + `_qa/questions.json` —— 手写问句问规则引擎（问题也是我手写）
- **不再有生成器**：以前那批 `batch*.py`（按组批量生成 cue）与 `anim_framing.py`（机械算取景）
  **已退役**，只作为历史留档在 `.claude/anim_batches/`。要改动画就**直接改 full.json**。

## 一条 cue 要做的其实只有两件事（用户 2026-09-20 的定调）

用户原话：「我们不是在做什么 3D 模型的骨骼动画，而是在**维护一组组件的创建/销毁，以及它们的
状态流转**。所谓的『动画』**只不过是在套原语而已**。」

所以写一条 cue = **两个判断 + 套原语 + 写契约**：

1. **状态**：这一句口播要改变什么？（规则上是什么事）
   套原语写下来：`create`（从盒里出来）/ `destroy`（放回盒子）/ `transfer`（zone→zone，
   带 `what`+`quantity`+短句即可）/ `to: face_up|face_down`（写终态，不写"翻一下"）/
   `stack`（搭一摞）/ `showbox`（整幅图）/ 以及表现层 `highlight`/`point`/`fade`/`scale`。
   **没有状态变化就空着** —— 该有的有、不该有的就没有。
2. **取景**：这一句要看哪几个 zone？`camera` 写那几个 zone，`camera_fill` 给填充率
   （默认 0.8；特写给 0.6~0.72；跨度超过整桌长/宽 50% 时引擎自动退回全局）。
3. 然后把 `enter`/`exit` 写清（**镜头里会出现的 zone 顺手声明**），跑四道检查，看一眼，定稿。

**位移/时长/缓动不用我设计** —— 我只声明"状态在什么时刻变成什么"，补间是引擎的事 ✓。
**"好不好看"由用户验收**；我写错了就改，改完这条 cue 即成为这一版的固定资产 ✓。

## 改一条 cue 的标准动作（用户 2026-09-21 定调：**必须从脚本开始走流程**）

以后任何动画内容修改，强制按这个顺序，**不许一上来就改 events/Unity**：

1. **先改脚本的“文字版”**：
   - `story`：这条口播在说什么
   - `note`：为什么这么演
   - `tree`：哪棵树、是否切树；组件介绍天然只放该组件
   - `enter`/`exit`：画面入口/出口要变成什么
   - `camera`：要看哪几个 zone + `camera_fill`
   - 这一轮**先不写 `events`**；文字与结构先对齐。
2. **调 BoardAI API 确定合法性**：
   - 把文字里的状态变化转成最小合法性问句，先问规则引擎；
   - 工具：`scripts/qa_anim_ask.py` / `_qa/questions.json`（问句手写）；必要时用 `qa_anim_check`。
3. **再套原语**：
   - 只把第 1 步的 `enter/exit` 翻译成 `create/transfer/destroy/stack/showbox/...`；
   - 不得在这一步改变语义；状态清理必须归到口播真正说它的那条 cue。
4. **对账与验收**：
   - `python3 scripts/validate_cue_anim.py`
   - `python3 scripts/validate_anim_rules.py`
   - `python3 scripts/check_unity_scripts.py`
   - `./scripts/dump_states.sh && python3 scripts/check_cue_script.py --all`
   - 全绿后 commit，再交用户做视觉验收。

**教训**：先改 events、后补文字/树，会把“状态收尾挂到语义无关 cue”“口播讲贵族、画面却做宝石”这类错误写进数据；
而契约与引擎会一起自洽，所有现有对账都会全绿。
