# Rules search performance probe

From the repository root:

```powershell
dotnet run --project tools/diagnostics/RulesSearchPerf -c Release -- D:\workspace\board
```

Pass your checkout's absolute path instead of the example path. This probe uses
real rule files and the production search facade, without an LLM or Qdrant.
Each game is warmed with ten fixed queries, then measured over five repetitions.
It reports mean/P50/P95 wall-clock search time and a hash of full serialized
results (including ordering and scores). Compare hashes before interpreting
speedups. `bytes_per_search` counts process-wide allocations over the measured
loop, including result serialization for hashing; it is not retained cache size.

Run without concurrent builds/tests for comparisons. These are warm keyword-only
microbenchmarks, not cold-start, multi-user or end-to-end chat latency results.
Console build warnings may precede the JSON array. The query sample exercises
generic terms, multiple terms, a namespace and a miss; it is not a quality eval.
