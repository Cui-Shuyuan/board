# 问答后端缓存架构升级

## 范围

本轮只改规则问答后端的缓存边界、生命周期和可观测性。模型、思考设置、提示词、
工具调用轮数、检索阈值、排序语义和规则数据均未调整。未重启或部署正在运行的 API。

## 真值与缓存关系

正式真值始终是 `content/ontology/` 与 `content/games/{game}/` 下的规则 JSON。

- 每次请求通过 `RulesSnapshotScope` 绑定一个 `RulesGameSnapshot`；同一请求内的名称
  索引、流程索引、事实计算和关键词材料都引用这一版快照。
- 规则快照内的文档是该版本独立读取的 `JsonDocument`。内存淘汰只移除缓存强引用，
  不 Dispose 仍可能被飞行中请求持有的旧文档；请求结束后由 GC 回收。
- 公共 ontology（`concepts.json` + 可选 `flow.json`）作为独立共享对象管理，游戏快照
  只引用它。缓存统计只计一次 ontology 字节，不按游戏重复计。
- 关键词搜索材料是规则的纯派生值，不含用户问题、聊天或答案。内存/磁盘材料都必须
  先匹配游戏、格式版本、构建算法版本、配置指纹和规则内容 SHA-256。

## 缓存开关与预算

`backend/BoardAI.Api/appsettings.json` 的默认配置：

```json
"Rules": {
  "MemoryCache": {
    "Enabled": true,
    "BudgetBytes": 268435456,
    "HighWatermark": 0.80,
    "TargetWatermark": 0.60,
    "MaxDerivedEntriesPerGame": 32
  },
  "DiskCache": {
    "Enabled": true,
    "Directory": "",
    "BudgetBytes": 536870912,
    "FlushIntervalSeconds": 300,
    "ShutdownFlushTimeoutMilliseconds": 5000,
    "PendingWriteLimit": 64,
    "PendingBytesLimit": 67108864
  }
}
```

- 同时设置 `Rules:MemoryCache:Enabled=false` 与 `Rules:DiskCache:Enabled=false` 即为
  关闭跨请求缓存的模式；每次请求仍会从规则文件构建自己的快照和请求内计算。
- 只关内存、保留磁盘时，每次冷请求可从磁盘恢复搜索材料，但不会长期保留游戏快照。
- 预算、高水位、目标水位和磁盘刷写参数会校验；必须满足
  `0 < TargetWatermark < HighWatermark <= 1`、预算为正。
- 预算单位是估算缓存占用，不是进程硬内存上限，也不代表淘汰后进程 RSS 立即下降。
- `Directory` 为空时默认使用
  `$BOARD_RUNTIME_ROOT/cache/rules-derived-cache`。
  也可配置绝对目录，或相对 `BOARD_RUNTIME_ROOT` 的目录。

## 内存所有权与估算

- 游戏快照按最近访问时间做 LRU。达到高水位时先淘汰最久未访问的游戏，降到目标水位；
  游戏都淘汰后仍超额时才淘汰公共 ontology。
- 单个快照大于内存预算时，本次请求仍计算和使用，但不进入长期缓存。
- 关键词 corpus 的长期 key 只有“全量/ontology 命名空间”两种，不按用户查询文本创建；
  请求内同 query 复用仍由 `RetrievalRequestContext` 负责。
- 估算口径：原始 `JsonDocument` 按 UTF-8 原文大小约 2 倍加对象头估算；派生 corpus
  按字符串、媒体 JsonElement 和集合对象逐项估算。估算包含主要持有对象，不是 GC 精确
  报告。`/api/rules/cache/stats` 另行返回 managed heap 与 working set，二者不混称为
  “缓存预算”。
- 请求正在使用的旧快照不计入“受管理缓存估算”，但会出现在进程实际内存中；淘汰后
  进程内存下降取决于 GC 和请求生命周期。

## 失效、淘汰与降级流程

1. 请求取快照前比较 ontology、游戏 concepts/instances/flow 的存在性、size 和 mtime。
2. 未命中时只锁单个游戏加载槽，重读文档并比较读取前后签名；成功后以内容哈希生成
   `Version`，原子替换缓存引用。
3. 搜索 corpus 首次构建时按完整 `Version` 查磁盘；身份不匹配、格式不兼容、截断或
   payload hash 不符都丢弃磁盘条目并回退正式规则计算。
4. 计算完成的关键词材料写入有界待刷写集合；后台每五分钟刷写，写临时文件后同目录
   原子替换正式文件。待刷写集合按条目数和字节数封顶，丢弃只影响下次恢复速度。
5. 正常关闭最多等待配置的超时时间做一次尽力刷写。磁盘满、只读、损坏只记有界日志
   并降级，不伪装规则文件读取成功；规则文件缺失/非法仍抛原有错误。
6. 磁盘按总预算清理最久未访问文件到约 80%。启动不枚举加载全部文件，只有请求命中
   对应游戏版本时才读取。
7. 磁盘文件名为完整身份 SHA-256，游戏 ID 还经过严格字符校验，避免路径穿越；旧版本
   迟到的写入落在不同文件名，不能覆盖新版本。

## 可观测性

新增 `GET /api/rules/cache/stats`（只读诊断），返回：

- 内存/磁盘命中与未命中；
- 快照重建次数和平均耗时；
- 估算缓存字节、缓存游戏数、公共 ontology 数；
- LRU 淘汰次数、超大条目拒绝次数；
- 磁盘过期、损坏、读写失败、成功写入、清理删除、待刷写条目/字节；
- managed heap 与 process working set（作为独立实际内存指标）。

统计使用原子计数，不在每次命中写日志；磁盘故障日志按类别限次。

## Review 修复

- 配置层级已与文档和 `appsettings.json` 对齐：`RulesOptions` 直接绑定
  `Rules:MemoryCache` 与 `Rules:DiskCache`，不再多一层 `Rules:Cache`。新增测试使用
  文档中的配置 key 做真实 options 绑定，验证关闭开关和 1 MiB 预算确实生效。
- 派生缓存增长回调现在携带具体 `RulesGameSnapshot` / `RulesOntologySnapshot` 引用，
  只有该引用仍是当前缓存条目时才重新核算。缓存占用每次从实际保留的 ontology 和
  游戏快照重新求和，不再依赖可能被旧快照污染的累计 delta。新增回归测试验证：旧快照
  新增超大派生缓存不会改变新快照账面、不会触发错误淘汰，且新快照仍可命中。

## 测试结果

Release 构建，未停止现有 API：

- `BoardAI.Api.Tests`：285/285 通过，0 失败、0 跳过。
- 新增 6 个核心测试：
  1. 问答检索第一次冷构建、第二次走内存快照缓存且结果一致；
  2. 内存和磁盘缓存全部关闭时结果一致，且不保留游戏缓存；
  3. 将测试内存预算临时调小，验证最久未访问游戏被淘汰、最近访问游戏仍命中、
     再次访问已淘汰游戏会重建；
  4. 进程级缓存对象重建后，第二次服务实例从磁盘恢复关键词材料，结果与首次一致；
  5. 文档中的 `Rules:MemoryCache` / `Rules:DiskCache` key 能真实绑定并生效；
  6. 已淘汰旧快照的派生缓存增长不会污染新快照记账或触发错误淘汰。
- 测试中的小预算只存在于测试 options；`appsettings.json` 保持默认 256 MiB。
- 未运行扩大版性能基准，也未宣称整次问答延迟收益。已测试的是缓存命中与 LRU 行为；
  实际吞吐、长期 RSS、磁盘恢复耗时和多用户并发仍需在真实 API 部署环境验证。

## 未验证限制

- 文件 freshness 仍使用 size+mtime 的快速探测；极端情况下内容变化但 size 和 mtime
  完全不变的热路径需要重启或显式淘汰后才会重新读取。
- 内存预算估算不是 GC 精确值；进程 RSS 受运行时、模型、Qdrant 客户端和飞行中请求影响。
- 正常关闭刷写是尽力而为，超时后允许丢失未落盘优化。
- 本轮未改向量索引和 Qdrant 缓存；上述收益不能等同于含 LLM/向量的整次问答收益。
