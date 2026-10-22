# RespondPackProbe

冻结事实包后，只运行 Respond 阶段一次，用于隔离 Understand 与检索随机性。

```bash
dotnet run --project tools/diagnostics/RespondPackProbe -- \
  /mnt/d/workspace/board agricola \
  "收获阶段的喂养是怎么结算的？" $BOARD_RUNTIME_ROOT/tmp/pack.json $BOARD_RUNTIME_ROOT/tmp/respond.json
```

它复用生产代码中的 `BuildRespondMessages` 与 `submit_answer` schema，不运行 Understand、不补充检索。
