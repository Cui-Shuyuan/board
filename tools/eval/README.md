# 规则问答版本对比实验

本目录用于比较同一规则数据、同一模型配置下的旧/新 Board API 端到端表现。

> 运行时路径统一使用 `BOARD_RUNTIME_ROOT`；具体值在 git-ignored 的 `config/local.properties` 中赋值。
> 所有命令中的 `$BOARD_RUNTIME_ROOT/tmp/...` 是新的 scratch 路径；历史 `.tmp/...` 原始产物已压缩到
> `$BOARD_RUNTIME_ROOT/archive/agent-workspace-evidence-20261023-004025.tar.zst`，详见 `docs/ops/runtime-and-code-paths.md`。

## 历史耗时归因

已有 `runs.jsonl` 和带请求 ID 的两版 API 日志时，可以离线对齐，无需重发模型请求：

```powershell
python tools/eval/analyze_chat_latency.py --runs $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/main/runs.jsonl --old-log $BOARD_RUNTIME_ROOT/tmp/eval-old-api.log --new-log $BOARD_RUNTIME_ROOT/tmp/eval-new-api.log --out $BOARD_RUNTIME_ROOT/tmp/comprehensive-eval/latency-audit
python -m unittest discover -s tools/eval -p test_analyze_chat_latency.py
```

分析器适用于同一天、北京时间的现有日志格式，按问题、游戏和开始时间唯一匹配，匹配不完整会失败。LLM 区间从请求开始到后续首条编排日志，包含网络、排队、输入处理、推理、输出生成和反序列化；响应头到达不代表完整模型响应结束，不能把区间标成纯思考时间。工具区间包含调用及少量日志开销。原始 reasoning 仅写入忽略目录中的请求明细，用于行为分析，不是精确 token 或内部计算测量。

## 协议变体小对照（不调用 judge）

只切换 Thinking/ToolChoice 时，用同一题目文件和同一候选 API 跑各变体，再从原始 `runs.jsonl` 自动汇总：

```bash
python3 tools/eval/run_protocol_variant.py \
  --questions tools/qa/program_evidence_repair_dev_questions.jsonl \
  --api http://localhost:5002 --label C2-thinking-disabled-required \
  --thinking disabled --tool-choice required --out $BOARD_RUNTIME_ROOT/tmp/protocol-comparison/C2

python3 tools/eval/analyze_protocol_variants.py \
  --variant A=$BOARD_RUNTIME_ROOT/tmp/protocol-comparison/A-thinking-low \
  --variant B=$BOARD_RUNTIME_ROOT/tmp/protocol-comparison/B-thinking-disabled \
  --variant C2=$BOARD_RUNTIME_ROOT/tmp/protocol-comparison/C2 \
  --out $BOARD_RUNTIME_ROOT/tmp/protocol-comparison/summary.json
```

汇总把 plain final、非法终态校验失败、实际协议修复调用、token、延迟和时间线自耗时分开记录；它不判断答案质量。`runs.jsonl` 没有结构化 continuation handle，因此 continuation 次数仍需人工核对原始日志。

## 1. 冻结版本

历史检索对比使用 `36e4dbf` → `d8862ac`；程序控制问答编排对比使用：

- 基线：`a992cfe`
- 新路径：包含程序控制编排的当前提交


建议用 git worktree 分开构建：

```bash
git worktree add --force $BOARD_RUNTIME_ROOT/tmp/eval-old 36e4dbf
cd $BOARD_RUNTIME_ROOT/tmp/eval-old
dotnet build backend/BoardAI.Api/BoardAI.Api.csproj --nologo
```

两个服务必须使用同一份规则内容和同一个 Qdrant 索引。启动时显式指定：

```bat
rem Values come from git-ignored config/local.properties
set Rules__BasePath=%BOARD_WORKSPACE_WIN%
set BOARD_RUNTIME_ROOT=%BOARD_RUNTIME_ROOT_WIN%
set Embedding__ModelDir=%BOARD_RUNTIME_ROOT%\models\bge-base-zh-v1.5-fp32
```

旧版和新版分别运行在 `5001` / `5002`，启动后先做健康检查。基线 `a992cfe` 使用旧自由工具循环；当前实现默认仍为 `LLM:OrchestrationMode=legacy`，做 program 对照时必须显式把新实例设为 `program`。

## 2. 主实验

先为固定回归题集生成正式规则片段，再合并新增留出集：

```bash
python3 tools/eval/build_question_ground_truth.py \
  --questions tools/qa/comprehensive_eval_questions.jsonl \
  --api http://172.17.208.1:5002 \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/regression_ground_truth.json
python3 tools/eval/merge_question_sets.py \
  --main-questions tools/qa/comprehensive_eval_questions.jsonl \
  --main-ground-truth $BOARD_RUNTIME_ROOT/tmp/program-eval/regression_ground_truth.json \
  --holdout-questions tools/qa/program_orchestration_holdout.jsonl \
  --out-questions $BOARD_RUNTIME_ROOT/tmp/program-eval/questions_all.jsonl \
  --out-ground-truth $BOARD_RUNTIME_ROOT/tmp/program-eval/question_ground_truth.json
```

然后运行：

```bash
python3 tools/eval/compare_versions.py \
  --questions $BOARD_RUNTIME_ROOT/tmp/program-eval/questions_all.jsonl \
  --old-api http://172.17.208.1:5001 \
  --new-api http://172.17.208.1:5002 \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/main \
  --repeats 3 \
  --pair-workers 2
```

- 每条问题两版各重复 3 次。
- 运行器将两版请求成对发出，并随机化提交顺序。
- `--pair-workers 2` 表示同时有两对 old/new 请求在跑；两版承受相同并发负载。
- 原始响应、evidence、延迟、确定性命中写入 `$BOARD_RUNTIME_ROOT/tmp/.../runs.jsonl`。
- 网络错误最多重试一次，重试次数会留档；不会因回答不好而重试。
- 如果任务中断，给同一命令加 `--resume`，已完成的 `run_id` 会跳过。

## 3. 规则详情与盲评

先从冻结后的运行库生成每题可接受的规则证据片段：

```bash
python3 tools/eval/build_question_ground_truth.py \
  --questions tools/qa/comprehensive_eval_questions.jsonl \
  --api http://172.17.208.1:5002 \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/regression_ground_truth.json
python3 tools/eval/merge_question_sets.py \
  --main-questions tools/qa/comprehensive_eval_questions.jsonl \
  --main-ground-truth $BOARD_RUNTIME_ROOT/tmp/program-eval/regression_ground_truth.json \
  --holdout-questions tools/qa/program_orchestration_holdout.jsonl \
  --out-questions $BOARD_RUNTIME_ROOT/tmp/program-eval/questions_all.jsonl \
  --out-ground-truth $BOARD_RUNTIME_ROOT/tmp/program-eval/question_ground_truth.json
```

再从运行结果获取 matched/related 概念的补充描述和结构化费用/目标/条件字段：

```bash
python3 tools/eval/fetch_rule_details.py \
  --runs $BOARD_RUNTIME_ROOT/tmp/program-eval/main/runs.jsonl \
  --api http://172.17.208.1:5002 \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/main/rule_details.json
```

`rule_details.json` 现在保留 `structured_evidence`（cost/target/content/score 等嵌套正式字段），盲评提示会把这些字段和 description 一起注入；否则像 `play_bird` 的蛋费来源只存在于嵌套 cost 树里，会被 judge 误判为无依据。

盲评必须在有模型密钥的环境里运行，且不要打印密钥：

```bat
D:\Python\Python312\python.exe tools\eval\judge_answers.py ^
  --runs .tmp\program-eval\main\runs.jsonl ^
  --ground-truth .tmp\program-eval\question_ground_truth.json ^
  --rule-details .tmp\program-eval\main\rule_details.json ^
  --out .tmp\program-eval\main\judge.jsonl ^
  --model deepseek-v4-flash
```

第二个独立 judge：

```bat
D:\Python\Python312\python.exe tools\eval\judge_answers.py ^
  --runs .tmp\program-eval\main\runs.jsonl ^
  --ground-truth .tmp\program-eval\question_ground_truth.json ^
  --rule-details .tmp\program-eval\main\rule_details.json ^
  --out .tmp\program-eval\main\judge-deepseek-chat.jsonl ^
  --model deepseek-chat
```

保守合并：只有两个 judge 都 pass，才记为 pass。

```bash
python3 tools/eval/combine_judges.py \
  --judge $BOARD_RUNTIME_ROOT/tmp/program-eval/main/judge.jsonl \
  --judge $BOARD_RUNTIME_ROOT/tmp/program-eval/main/judge-deepseek-chat.jsonl \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/main/judge-consensus.jsonl \
  --summary $BOARD_RUNTIME_ROOT/tmp/program-eval/main/judge-consensus-summary.json
```

## 4. 分析

```bash
python3 tools/eval/analyze_compare.py \
  --runs $BOARD_RUNTIME_ROOT/tmp/program-eval/main/runs.jsonl \
  --judge $BOARD_RUNTIME_ROOT/tmp/program-eval/main/judge-consensus.jsonl \
  --out-dir $BOARD_RUNTIME_ROOT/tmp/program-eval/main/analysis-consensus

# 从服务日志统计 legacy 基线的真实模型调用数（新版也可交叉核对）
python3 tools/eval/analyze_model_calls.py \
  --baseline-log $BOARD_RUNTIME_ROOT/tmp/program-eval/baseline-api.log \
  --new-log $BOARD_RUNTIME_ROOT/tmp/program-eval/new-api.log \
  --baseline-start-utc <manifest-baseline-start> \
  --new-start-utc <manifest-new-start> \
  --out $BOARD_RUNTIME_ROOT/tmp/program-eval/main/model-calls.json
```

输出：

- `analysis.json`：完整数值；
- `summary.md`：总体、分游戏、分问题类别表；
- 终端会打印两版汇总。

## 5. 数据

- 固定回归题集：`tools/qa/comprehensive_eval_questions.jsonl`（85 条既有 execute-plan gold、13 条 intent 回归、7 条检索回归和 12 条负例/歧义题）。
- 新增留出集：`tools/qa/program_orchestration_holdout.jsonl`（16 条，含 slot、未知对象、必要澄清、多问题、同对象不同动作）。
- 主实验合并后 133 题，两版各 3 次，共 798 次聊天。
- 题集不存放生成答案；规则事实由运行库快照、规则 id 和盲评 rubric 分别验证。
- 原始 runs、judge 输出均为 QA 留档，默认放 `$BOARD_RUNTIME_ROOT/tmp/`，不提交 Git。
