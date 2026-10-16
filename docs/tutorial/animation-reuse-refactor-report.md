> 后续复核发现并已修复 R1–R6；以 `docs/tutorial/animation-reuse-review-response-20261017.md` 为准。本报告保留首版迁移记录。

# 动画素材复用重构报告（2026-10-17）

任务书：`docs/tutorial/animation-reuse-refactor-prompt.md`。
本报告只记录实际执行的改动与验证；“未执行”事项不会写成通过。

## 1. 最终数据关系

```text
content/games/<game>/tutorial/
  tracks/<track>.track.json
  sections/<section>/section.json
  sections/<section>/<track>.script.json      口播/beats/refs
  sections/<section>/<track>.anim.json        编排顺序 + 内联便利事件 + use/example
  sections/<section>/qa.json
  shared/stage/*.stage.json
  library/actions/*.action.json               只含状态原语
  library/examples/*.example.json             具体事实、稳定对象身份、variants
  library/presentations/*.presentation.json   只含表现原语
        │
        ▼ animation/section_source.py（内置 material_library 展开）
  build/<track>.anim.json / .script.json      装配中间产物（不进 Git）
        │
        ▼ animation/compile_tutorial.py
  <track>.runtime.json / anim/v2/<track>.compiled.json
```

- `section_source.py` 在分配 qualified cue id 之前展开 `use`/`example`，后续 schema、继承、编译、审计看到的仍是现有 v2 事件流。
- 展开记录 `MaterialUsage` 保留素材文件、调用 cue、步骤、绑定和时间来源；错误消息带有 `cue -> use -> step -> event` 链。
- 所有被引用素材文件参与 track `source_hash`；未引用素材不影响 track。共享 action/example 变更会让直接/间接调用者的 compiled 变 stale。
- 协议未改变：Unity 继续读取现有 `tutorial-anim-compiled/v2`，本次没有改播放器 C#。

## 2. 旧结构与删除/替换

- 没有增加旧格式双读、过渡适配器或第二套规则执行语言。
- 下列 section 内的重复状态序列被删除，替换为 `use`/`example` 或 presentation：
  - full `setup_cards/002.1`、quick `quick_setup_cards/001.2` 的市场铺设；
  - full `setup_gems/003.1`、`setup_gems/005.2`、quick `quick_setup_gems/002` 的宝石/黄金供应；
  - full `setup_players/nobles.001.2`、quick `quick_setup_players/001` 的贵族市场；
  - full `setup_players/starting.001.3`、quick `quick_setup_players/002` 的起始标记；
  - full `nobles/intro.001`、quick `quick_nobles/001` 的单贵族示例准备；
  - full `nobles/{trigger,forced,source,choice,repeat}.001*`、quick `quick_nobles/{002,003}` 的购牌、付款、补位、拿贵族动作；
  - full `setup_gems/001.1` / `002` 的五色宝石样本展示。
- “旧权威源”本来就是 section-first；本次没有保留第二份内联真值。`animation/archive/` 下的历史一次性迁移脚本未重新执行。
- `anim/v2/{full,quick}.compiled.json` 仍保留在 Git 中（当前消费者就是这样读取）；它们已用新源重新生成，除 `source_sha256` 外与迁移前逐字段一致。

## 3. 已共享的真实素材与调用位置

| 素材 | kind | full | quick |
|---|---|---|---|
| `splendor.example.initial_market` | example | `setup_cards/002.1` | `quick_setup_cards/001.2` |
| `splendor.action.seed_gem_supply` | action | `setup_gems/003.1` | `quick_setup_gems/002` |
| `splendor.action.seed_gold_supply` | action | `setup_gems/005.2` | `quick_setup_gems/002` |
| `splendor.action.seed_noble_market` | action | `setup_players/nobles.001.2` | `quick_setup_players/001` |
| `splendor.action.create_starting_marker` | action | `setup_players/starting.001.3` | `quick_setup_players/002` |
| `splendor.example.noble_single` | example | `nobles/intro.001`（variant `full_from_cards_intro`） | `quick_nobles/001`（variant `quick_from_setup`） |
| `splendor.action.seed_noble_hand_core` | action | 经 `noble_single` 间接调用 | 经 `noble_single` 间接调用 |
| `splendor.action.take_setup_gem` | action | `nobles/trigger.001.1`、`forced.001.1`、`source.001` | 经 `noble_single` 与付款前情间接调用 |
| `splendor.action.pay_and_take_market_card` | action | `nobles/{trigger,forced,source,choice}.001*` | `quick_nobles/{002,003}` |
| `splendor.action.draw_market_replacement` | action | `nobles/{trigger,forced,source,choice}.001*` | `quick_nobles/{002,003}` |
| `splendor.action.claim_noble` | action | `nobles/{trigger,source,choice,repeat}.001*` | `quick_nobles/{002,003}` |
| `splendor.presentation.five_gem_samples` | presentation | `setup_gems/{001.1,002}` | 未调用（quick 无宝石样本展示） |

统计：full 29 处、quick 16 处 `MaterialUsage`；库内 14 个素材。

## 4. 保留的版本差异及原因

- **单贵族前情**：full 从 `cards` 分支继承一张白色折扣卡与 3 枚宝石，另需补回 deck 顶牌；quick 从设置后的空玩家发展区出发，预置 2 蓝 2 白并调整市场槽位。差异被建模为 `splendor.example.noble_single` 的两个显式 variant，共享 4 红 + 3 黑的稳定卡牌身份。
- **付款计划**：full 在示例前情下有折扣，支付数量和宝石区不同；quick 支付 2 蓝 2 白。共享 action 用 `pay1/pay2` 参数表达，未把 full 折扣写死进 quick。
- **错误示范**：full `nobles/forced.001.1` 与正常分支共用购牌/补位动作，但有意识省略 `claim_noble`，保持独立 negative 状态来源；没有全局关闭检查。
- **多贵族示例**：quick `quick_nobles/003` 使用 `splendor.example.noble_multi` 的 `quick_double` variant；full 的多贵族选择前情和教学目标不同，当前仍保留 full 内联 setup，未为了统一而改教学语义。
- **宝石外观展示**：quick 已被审阅为不播五色宝石样本；presentation 只被 full 的两个 cue 使用。
- 这些差异没有改变旁白含义、教学对象或已审阅节奏；迁移后 compiled 与基线逐字段一致是主要回归证据。

## 5. 实际执行的验证

| 命令/检查 | 结果 |
|---|---|
| `python3 -m unittest discover -s animation -p 'test_*.py'` | **203/203 OK**（原 188 + 素材库 11 + 非卡牌夹具 4） |
| `python3 animation/material_cli.py validate --game splendor` | OK，14 materials loaded |
| `python3 animation/check_materials.py --game splendor` | full 29 usages / quick 16 usages，各 42 项前后置与 example 事实检查；quick_nobles/003 的 example setup 后仍有购买动作，checker 明确 NOTE 跳过“cue 末尾 facts 对比” |
| `python3 animation/section_source.py --game splendor --track {full,quick} --check` | OK，67 / 24 cues |
| `python3 animation/compile_animation_v2.py --game splendor --track {full,quick} --check` | 两轨 up to date |
| `python3 animation/check_anim_v2.py` | 两轨 0 warnings |
| `python3 animation/validate_anim_rules_v2.py` | 两轨 0 warnings |
| `python3 animation/audit_anim_v2.py` | full 53 cues 0 errors；quick 17 cues 0 errors |
| `python3 animation/check_card_identity_v2.py` | full 553 states、quick 333 states 0 error |
| 迁移前后 compiled 对账 | 除 `source_sha256` 外 **0 differences**；state/end/first_state、state_ops timing、clips、duration、camera 全等 |
| `python3 animation/compile_tutorial.py --game splendor --track {full,quick} --dry-run` | changed text sections = 0，未重新合成 TTS |
| `python3 tools/content/validate_rules.py --errors-only` | 0 errors / 72 warnings |
| `python3 tools/ops/check_no_secrets.py` | OK |
| `python3 tools/ops/check_animation_change_log.py` | OK，48 entries |
| `python3 tools/ops/check_unity_scripts.py` | 35 个 C# 文件编译通过（未改播放器，仅回归） |

非卡牌夹具：

- `tests/fixtures/worker_toy/tutorial/`：3 actions / 1 example / 2 tracks。
- `animation/test_worker_toy_fixture.py` 编译 detailed/compact 两轨，断言同模板 worker 多个实例 id 不同、绑定到不同行动格、资源数量按参数生效、机位和时长不同、共享 action 修改使两轨 source_hash 同时失效、未引用素材不失效。
- 该夹具不使用 Splendor 概念、卡牌、五色宝石或 TTS；只验证正式 schema、parser、material expander、状态装配、时间绑定和 compile 链。

## 6. 未执行 / 需剩余验收

- **Board API 合法性 QA 未在本轮重新发送**：`http://127.0.0.1:5000` 未运行。迁移后的 compiled 与迁移前逐字段一致，所以没有新增规则事实；但独立裁判层本轮没有产生新日志留档。
- **Unity 采样/截图视觉验收未执行**：本轮没有可用的 Unity 运行环境；只跑了 `check_unity_scripts.py`。
- **Android 真机端到端未执行**：与本次重构无关链路，仍按项目原验收待办处理。
- **quick_nobles/003 的 example setup 后 facts**：checker 因 cue 后续仍有购牌状态变化而输出 NOTE，改为人工/后续增强“阶段快照对比”；没有把它报告成通过。
- `full` 多贵族前情的完整示例化暂未做，保留为差异。

## 7. 关键文件与命令

新增：

- `animation/material_library.py`：`tutorial-material/v1` 加载、角色/参数/依赖/循环校验、`use` 展开、命名空间隔离、anchor 或显式局部时间绑定、usage 来源记录。
- `animation/check_materials.py`：用 compiled state 校验 action pre/post 条件和 example facts。
- `animation/material_cli.py`：`list` / `validate` / `expand-cue` / `impact [--check|--rebuild]` / `stale`。
- `animation/test_material_library.py`、`animation/test_worker_toy_fixture.py`。
- `tests/fixtures/worker_toy/tutorial/`、`content/games/splendor/tutorial/library/`。
- `docs/tutorial/animation-materials-authoring-guide.md`：作者指南、模板和经济模型提示词。

关键命令：

```bash
python3 animation/material_cli.py validate --game splendor
python3 animation/check_materials.py --game splendor
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_gem_supply --check
python3 animation/material_cli.py impact --game splendor --material splendor.action.seed_gem_supply --rebuild
python3 animation/material_cli.py expand-cue --game splendor --track quick --cue quick_nobles/001 --anchors
python3 animation/compile_tutorial.py --game splendor --track quick --skip-tts
python3 animation/compile_animation_v2.py --game splendor --track quick --check
python3 -m unittest discover -s animation -p 'test_*.py'
```

## 8. 工作区保留

- 本任务开始前已有的 `content/games/splendor/tutorial/anim/_qa/ask_log_*.{jsonl,md}` 未提交 QA 留档原样保留，未清理、未 stage。
- 基线快照保存在 `.tmp/tutorial-reuse-baseline-20261017-002026/`（本地 scratch，不入 Git），包含迁移前 source、compiled、build、文件 hash、Git 状态和 HEAD。
