# 规则问答版本对比实验

本目录用于比较同一规则数据、同一模型配置下的旧/新 Board API 端到端表现。

## 历史耗时归因

已有 `runs.jsonl` 和带请求 ID 的两版 API 日志时，可以离线对齐，无需重发模型请求：

```powershell
python tools/eval/analyze_chat_latency.py --runs .tmp/comprehensive-eval/main/runs.jsonl --old-log .tmp/eval-old-api.log --new-log .tmp/eval-new-api.log --out .tmp/comprehensive-eval/latency-audit
python -m unittest discover -s tools/eval -p test_analyze_chat_latency.py
```

分析器适用于同一天、北京时间的现有日志格式，按问题、游戏和开始时间唯一匹配，匹配不完整会失败。LLM 区间从请求开始到后续首条编排日志，包含网络、排队、输入处理、推理、输出生成和反序列化；响应头到达不代表完整模型响应结束，不能把区间标成纯思考时间。工具区间包含调用及少量日志开销。原始 reasoning 仅写入忽略目录中的请求明细，用于行为分析，不是精确 token 或内部计算测量。

## 1. 冻结版本

当前对比使用：

- 旧版：`36e4dbf`
- 新版：`d8862ac`

建议用 git worktree 分开构建：

```bash
git worktree add --force .tmp/eval-old 36e4dbf
cd .tmp/eval-old
dotnet build backend/BoardAI.Api/BoardAI.Api.csproj --nologo
```

两个服务必须使用同一份规则内容和同一个 Qdrant 索引。启动时显式指定：

```bat
set Rules__BasePath=D:\workspace\board
set Embedding__ModelDir=D:\workspace\board\backend\BoardAI.Api\ml_models\bge-base-zh-v1.5-fp32
```

旧版和新版分别运行在 `5001` / `5002`，启动后先做健康检查。

## 2. 主实验

```bash
python3 tools/eval/compare_versions.py \
  --questions tools/qa/comprehensive_eval_questions.jsonl \
  --old-api http://172.17.208.1:5001 \
  --new-api http://172.17.208.1:5002 \
  --out .tmp/comprehensive-eval/main \
  --repeats 3
```

- 每条问题两版各重复 3 次。
- 运行器将两版请求成对发出，并随机化提交顺序。
- 原始响应、evidence、延迟、确定性命中写入 `.tmp/.../runs.jsonl`。
- 网络错误最多重试一次，重试次数会留档；不会因回答不好而重试。
- 如果任务中断，给同一命令加 `--resume`，已完成的 `run_id` 会跳过。

## 3. 规则详情与盲评

先从冻结后的运行库生成每题可接受的规则证据片段：

```bash
python3 tools/eval/build_question_ground_truth.py   --questions tools/qa/comprehensive_eval_questions.jsonl   --api http://172.17.208.1:5002   --out .tmp/comprehensive-eval/question_ground_truth.json
```

再从运行结果获取 matched/related 概念的补充描述：

```bash
python3 tools/eval/fetch_rule_details.py \
  --runs .tmp/comprehensive-eval/main/runs.jsonl \
  --api http://172.17.208.1:5002 \
  --out .tmp/comprehensive-eval/main/rule_details.json
```

盲评必须在有模型密钥的环境里运行，且不要打印密钥：

```bat
D:\Python\Python312\python.exe tools\eval\judge_answers.py ^
  --runs .tmp\comprehensive-eval\main\runs.jsonl ^
  --ground-truth .tmp\comprehensive-eval\question_ground_truth.json ^
  --rule-details .tmp\comprehensive-eval\main\rule_details.json ^
  --out .tmp\comprehensive-eval\main\judge.jsonl ^
  --model deepseek-v4-flash
```

第二个独立 judge：

```bat
D:\Python\Python312\python.exe tools\eval\judge_answers.py ^
  --runs .tmp\comprehensive-eval\main\runs.jsonl ^
  --ground-truth .tmp\comprehensive-eval\question_ground_truth.json ^
  --rule-details .tmp\comprehensive-eval\main\rule_details.json ^
  --out .tmp\comprehensive-eval\main\judge-deepseek-chat.jsonl ^
  --model deepseek-chat
```

保守合并：只有两个 judge 都 pass，才记为 pass。

```bash
python3 tools/eval/combine_judges.py \
  --judge .tmp/comprehensive-eval/main/judge.jsonl \
  --judge .tmp/comprehensive-eval/main/judge-deepseek-chat.jsonl \
  --out .tmp/comprehensive-eval/main/judge-consensus.jsonl \
  --summary .tmp/comprehensive-eval/main/judge-consensus-summary.json
```

## 4. 分析

```bash
python3 tools/eval/analyze_compare.py \
  --runs .tmp/comprehensive-eval/main/runs.jsonl \
  --judge .tmp/comprehensive-eval/main/judge-consensus.jsonl \
  --out-dir .tmp/comprehensive-eval/main/analysis-consensus
```

输出：

- `analysis.json`：完整数值；
- `summary.md`：总体、分游戏、分问题类别表；
- 终端会打印两版汇总。

## 5. 数据

- 题集：`tools/qa/comprehensive_eval_questions.jsonl`
- 题集由 85 条既有 execute-plan gold、13 条 intent 回归、7 条检索回归和 12 条负例/歧义题组成。
- 题集不存放生成答案；规则事实由运行库快照、规则 id 和盲评 rubric 分别验证。
- 原始 runs、judge 输出均为 QA 留档，默认放 `.tmp/`，不提交 Git。
