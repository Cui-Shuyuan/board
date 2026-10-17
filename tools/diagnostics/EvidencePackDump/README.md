# EvidencePackDump

用于把固定子问题计划直接送入事实包构建器，输出实际序列化包，不调用回答模型。

```bash
dotnet run --project tools/diagnostics/EvidencePackDump -- \
  /mnt/d/workspace/board agricola .tmp/plan.json .tmp/pack.json 60000
```

`plan.json` 是 `[{ "question_id": "q1", "entity": "...", "relation": "...", "intent": "..." }]`。
该工具使用反射访问 internal 构建器，因此可把同一源码复制到旧提交的 worktree 中，对冻结计划做前后包对照。
