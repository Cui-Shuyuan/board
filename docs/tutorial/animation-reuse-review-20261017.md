# 动画素材重构复核与修复任务（2026-10-17）

复核提交：`9169018`、`290186d`。这是对本次实现的后续修复任务，不要求推翻已完成的素材化迁移。

结论：可接受为保持现有播放行为的首版素材抽取；尚不能视为原任务书要求的完整可复用生产管线。除外部 QA/Unity 环境验收外，还有以下代码缺陷及自动化验收缺口。

## 已独立确认

- 当前 full/quick compiled 与 `aee93fa` 中对应产物比较，移除 `source_sha256` 后，两者分别完全相等。这是本次结构迁移没有改变现有播放数据的有效证据。
- `test_material_library.py` 的 11 项和 `test_worker_toy_fixture.py` 的 4 项测试通过。
- `check_materials.py --game splendor` 返回成功，输出 full 29 usages、quick 16 usages，各 42 checks；同时确实跳过了 `quick_nobles/003` 的 example facts。
- 本机用 `D:/msys64/ucrt64/bin/python.exe` 重跑全量 unittest：报告运行 199 tests，其中一个模块导入错误为缺少 `requests`，其他执行的测试通过。本次未能独立复现报告中的 203/203；这是当前 Python 环境依赖问题，不能据此认定原报告虚假，也不应把它算成本次功能回归。
- 已有的 14 个未提交 QA 文件未被修改。

## 必须修复的具体问题

### R1 [P1] 契约检查必须位于调用边界，不能跳过中间状态

位置：`animation/check_materials.py:257`、`:272`、`:285`。

当前所有 action 后置条件都与整个 cue 的 `end_state` 比较。前置条件若涉及之前被修改过的区域，就跳过对应区域；example 后面还有任何状态操作，则跳过该 example 的事实检查。

这会误判合法组合，也会漏掉非法入口。例如一个动作创建组件后由下一个动作移走，前一个动作“创建完成”的后置条件在 cue 末尾自然不成立；反过来，不合法的中间状态可能被后续动作修复，从终态看不出来。

修复要求：为每次调用和必要的示例阶段保留准确边界，按实际执行顺序取得入口和出口快照。不要以事件字典相等查找首次位置来识别调用：两个相同调用可能匹配到同一个事件。应使用稳定的调用/步骤/事件来源标识，并明确同时间事件和不同时间锚点的顺序。

验收：覆盖“创建后转移”“前一个动作使后一个前提失效”“后续修复不能掩盖先前错误”，以及 `quick_nobles/003` 的 setup 后、购买前 facts；不得继续以 NOTE 跳过必要检查而整体返回成功。

### R2 [P1] 嵌套 action 的契约与来源记录被丢弃

位置：`animation/material_library.py:767`、`:815`。

`_expand_asset` 使用 `nested, _, nested_files` 丢弃嵌套调用的 usage。`_expand_nested_use` 创建的 usage 本身也没有解析后的 contract。后续 checker 只能看到顶层 usage。

实测：用测试已有的 `example_doc()` 包装带 postconditions 的 `fake.seed`，展开后的记录只有 `('fake.round', None)`，没有 `fake.seed` 的契约。非卡牌夹具大量通过 example 嵌套动作，其动作契约同样没有进入此检查列表。

修复要求：递归保留所有调用、绑定、契约、变体和事件来源，并接入 R1 的调用边界验证；报告应能追到完整调用路径。不能用父 example 的终态检查替代子动作前后置检查。

验收：顶层使用 action 与把同一 action 包装进 example/action 后，非法入口与非法结果都必须失败；至少测试两层嵌套。

### R3 [P1] presentation 可通过嵌套 action 修改状态

位置：`animation/material_library.py:317` 附近的 kind 检查及 `_expand_nested_use`。

kind 限制只检查素材自身 `steps[].events`，没有检查 `steps[].use` 引入的操作。

实测：定义 kind 为 presentation 的 `fake.pair`，让其步骤引用 `fake.seed` action。库加载成功，展开结果包含 `create`，没有报错。因此“表现不能改状态”的关键边界可被合法嵌套语法绕过。

修复要求：在依赖边界或展开后的实际原语层递归验证 kind 约束。action 通过嵌套 presentation 引入纯表现操作的对称情况也要检查。example 可组合什么必须明确，而不是靠顶层字段名推断。

验收：直接、间接和多层嵌套都不能突破类别约束，错误指向具体引用链。

### R4 [P2] 嵌套调用共用 namespace，临时对象冲突

位置：`animation/material_library.py:699`、`:809`、`:979`。

顶层调用创建 namespace；嵌套调用复用同一个 `_ExpansionContext`，只替换 timing/omit，没有创建子调用命名空间。

实测：一个 presentation 的步骤连续引用两次测试已有 `fake.show`，两次 `target.id` 都为 `u1__pin`。这可能使第二个显示覆盖第一个，或者让隐藏/替换标注命中另一次调用。

修复要求：命名空间由稳定的调用路径确定，支持需要显式共享的实体/对象绑定。拒绝显式别名以及规范化后的别名碰撞；检查嵌套 timing 计数器等可变上下文是否也存在串用。不得用随机 ID 破坏可重复构建。

验收：同素材两次嵌套、不同父素材嵌套同子素材、显式别名冲突都被覆盖，真实实体身份不受临时对象重命名影响。

### R5 [P2] QA 的 HEAD 基线未复制 library，导致无修改也判变化

位置：`animation/compile_tutorial.py:159`。

`_load_section_baseline_from_head` 只取 tracks/shared/sections，漏掉新增加的 library。临时基线因此没有展开共享动作，与当前完整展开的文档不一致。

实测，在无已跟踪文件修改的工作区：

- `quick_setup_cards/001.2` 当前展开有 23 个 events，HEAD 基线只剩 5 个。
- `select_qa_changed_ids(current, baseline, [])` 返回 7 条 quick cue：`quick_setup_cards/001.2`、`quick_setup_gems/002`、`quick_setup_players/001`、`quick_setup_players/002`、`quick_nobles/001`、`quick_nobles/002`、`quick_nobles/003`。

修复要求：用完整历史依赖闭包构建 HEAD 基线，不能借用当前工作区 library；同时在应该展开素材却缺少库时明确失败，避免悄悄生成仅有内联事件的基线。QA 变化签名应覆盖共享事实/契约变化与受影响调用，不只是展开后的状态 events。

验收：未修改时变化列表为空；只改共享状态/契约能选中相应调用；只改无关素材不波及这些 cue；历史基线不能读取修改后的素材。

### R6 [P2] contains 的数量字段错误参与单对象匹配

位置：`animation/check_materials.py:134`、`:135`。

checker 提取 selector 的 `count` 作为期望数量后，仍把含 `count` 的整个 selector 传给 `_item_matches`。后者没有 `count` 字段映射，因此任何带 count 的 selector 都无法匹配。

实测：box 中确有一个 TemplateId 为 worker 的组件，断言 `contains: [{"template":"worker","count":1}]` 仍报 matched 0 / expected 1。

修复要求：分离对象属性选择器与匹配数量约束；同时修正 count=0 时仍落入 missing-item 报错的问题，校验非法数量类型和范围。

验收：匹配数量 0/1/多于 1、存在性选择器和数量不符都覆盖。

## 对原任务书仍需补充的验收证据

以下不与上面的已复现缺陷混为一谈，需要核对现有能力后补实现或准确说明缺口：

1. **相同事实、不同节奏。** 非卡牌夹具当前 detailed 获得 2 资源、compact 获得 3 资源，证明的是参数化。应另外让相同输入事实在两种表现/时间绑定下得到相同语义结果；保留参数化测试作为独立测试。资源不同的测试不能替代此目标。
2. **阶段事实与实体身份。** `_item_matches` 忽略 `facts.objects.id`，且每个声明独立执行 `any(...)`；不能证明两个声明对应两个不同实体。需要明确逻辑 ID 到实际实体的绑定和唯一匹配语义，而不只检查模板相似。
3. **时间预算。** 语义 marker 到锚点的映射已实现，但还需用反例证明因果顺序、同实体冲突、动作最小时长、超出可用窗口，以及显式 hold 的实际时长均被检查。不能把基础 anchor 解析等同于完成节奏约束。
4. **连续/独立示例。** `mode=isolated` 当前仅检查是否存在 entry/parent；需要证明其确实建立正确状态边界和恢复方式，而不是只允许写一个标记。
5. **构建失效。** 当前 source_hash 包含源文件；还需覆盖影响产物的编译器/schema 与有效音频 timing 配置变化。应检验实际 stale/重建行为，不只检验字典整体不相等。
6. **主流程门禁。** 新的素材状态 checker 应接到作者日常构建或统一验收入口，确保失败返回非零且失败产物不被标作最新成功结果。当前 standalone checker 的存在不自动意味着构建已经强制执行。

## 执行方式

保留这次已成功的素材迁移，不扩大到全套重写。先为 R1–R6 添加能暴露现有错误的最小行为测试，再修复；随后补上述与复用核心目标直接相关的验收。保持 Splendor 已审核播放语义；非必要情况下不重新生成音频，不改变旁白。

继续使用原任务书中的工作区保护、QA、动画日志和视觉验收约定。无需为未改动画行为的纯校验器修复虚构画面调整记录。最后报告各项修复证据，并将“代码实现完成”和“外部/人工验收完成”分开。Board API 或 Unity 不可用时如实保留待验收，不以 compiled 相等替代所有未来组合的正确性验证。
