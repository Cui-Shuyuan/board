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

## 改一条 cue 的标准动作

1. 手写/修改这条 cue：`story`/`note`/`timing` + `enter`/`exit` + `start`/`events`
2. 取景：`camera` 写要入镜的 zone（逗号分隔），`camera_fill` 给填充率（默认 0.8；
   想更近给 0.6~0.72；跨度超过整桌长/宽 **50%** 时引擎会自动退回全局镜头）
3. **镜头里会出现的 zone，契约里顺手声明掉**（这是硬要求：漏了 validate 会报，
   对账也会把"入镜但没声明"标出来）
4. 跑四道检查（上面那四个），全绿后 commit —— 这一步之后它就是这个版本的固定资产
