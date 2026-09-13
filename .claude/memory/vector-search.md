---
name: vector-search
description: 向量语义检索架构——Qdrant + bge-base-zh-v1.5 ONNX 的部署与使用方式（2026-08-16 从小模型升级）
metadata:
  type: project
---

# 向量语义检索

## 问题背景

原关键词搜索 `SearchConcepts` 使用空白/标点分词 + 子串匹配，无法处理语义相近但字符不同的查询。例如 "白色骰子" 无法匹配 "白色的六面骰"。

## 架构

```
客人问题 → LLM 调 search_concepts
              ↓
  ChatOrchestratorService → GameRulesService.SearchConceptsAsync
              ↓
  ① VectorSearchService.SearchAsync (Qdrant 余弦相似度, top-10)
     ↓ 失败或无结果
  ② KeywordSearch (原有降级)
```

三层构件，各自独立：

| 构件 | 形式 | 位置 |
|---|---|---|
| Qdrant 向量数据库 | 独立进程 (`qdrant.exe`) | `D:\qdrant\` |
| Embedding 模型 | ONNX 文件，C# 推理 | `ml_models/bge-base-zh-v1.5-fp32/`（int8 版、small 版保留可回滚） |
| 重建索引脚本 | Python，不依赖 .NET | `scripts/rebuild_index.py` |

## Qdrant

- 版本 1.18.3，Windows 原生二进制，单文件
- gRPC 端口 6334（.NET 客户端），HTTP 端口 6333（Python 脚本用）
- 数据持久化：`D:\qdrant\data\`，配置通过环境变量 `QDRANT__STORAGE__STORAGE_PATH`
- **每个游戏一个 collection**：`board_{gameId}`（如 `board_civolution`、`board_splendor`）
- 每个 collection 里存该游戏的全部概念向量（含 ontology 概念副本）

## Embedding 模型

- **bge-base-zh-v1.5 (BAAI)，768 维，fp32 ONNX**（2026-08-16 由 bge-small-zh 512 维升级到 base，同日 int8→fp32）
- fp32 来源：`BAAI/bge-base-zh-v1.5` 官方 pytorch 权重，`optimum-cli export onnx --library transformers --task feature-extraction` 导出（388MB 单文件 model.onnx）。注意：仓库含 modules.json，导出时必须加 `--library transformers` 否则 optimum 按 SentenceTransformer 加载报错。目录 `ml_models/_tmp_bge_fp32/` 保留 torch 源权重
- **int8 vs fp32 对照（scripts/_embed_gap_fp32.py，8 查询 × 216 概念）**：fp32 top1 平均只高 ~0.013，差距分布基本不变——int8 量化不是「正确概念与干扰项拉不开差距」的根因。真正的根因是近义概念名（播种 vs 有谷物/谷物 差 0.005；计分 vs 计分纸 差 0.02）+ BERT 各向异性（随机文档对均值 0.37/p95 0.55）
- **QA 三轮对照**：int8 A43/C10/E2；fp32 A39/C14/E2 → A46/B1/C6/E1。逐题比对 12 个翻转题全部是 LLM 查询措辞随机性（「职业卡」vs「职业」、是否直接敲概念 id），向量分数两版逐题接近。**QA 成绩波动主要来自 LLM 查询生成，单轮 QA 数字不可信，需多轮**
- 升级原因（用户不满小模型区分度）：small 窄锥噪声 0.80–0.84 vs 信号 0.85–0.89（间距 ~0.05 ≈ 随机）；base 噪声 0.36–0.47 vs 信号 0.53–0.73（间距 0.2）。基准脚本 `scripts/_bench_embedding_models.py`，结论：经典别名（杜布隆/市长/殖民者）两模型语义匹配都失败——别名必须走数据层精确匹配，向量只对自然语言转述有效
- **BERT 系输入差异**：base 需要 `token_type_ids`（全零）——EmbeddingService 与 rebuild_index.py 均按模型签名条件添加
- **所有模型统一放 `ml_models/`，整个目录 gitignore**（用户明确要求：模型不进 git）
- 分词器：`tokenizer.json` + `vocab.txt`，C# 端用 `Tokenizers.HuggingFace` NuGet 加载，Python 端用 `transformers.AutoTokenizer`
- 换模型 = 拷文件进 `ml_models/` + 改 `appsettings.json` 的 `Embedding.ModelDir` + **全量重建所有游戏索引**（collection 维度随模型变化）

## 索引内容

每款游戏的索引覆盖：
- `ontology/concepts.json` → concepts 数组
- `ontology/flow.json` → trigger_pipeline 执行流程
- `games/{game}/concepts.json` → objects / actions / triggers / conditions / top_level_refs
- `games/{game}/flow.json` → procedures 树（递归展开，含嵌套 children 和 events）

每条记录的搜索文本由 `id + name.zh + name.en + definition.zh` 拼接而成。

## C# 端关键文件

| 文件 | 职责 |
|---|---|
| `Services/EmbeddingService.cs` | 加载 ONNX 模型 + Tokenizer，`Embed(string) → float[]` |
| `Services/VectorSearchService.cs` | 封装 Qdrant.Client，建索引 / 搜索 / 删索引 |
| `Services/GameRulesService.cs` | `SearchConceptsAsync` 向量优先+关键词降级；`GetIndexItems` 提取全量概念+flow；`BuildEmbeddingIndexAsync` 触发重建 |
| `Program.cs` | 支持 CLI 模式：`--rebuild-all` / `--rebuild-index <game>` |
| `Controllers/RulesController.cs` | `POST /api/rules/admin/rebuild-index/{game}` 和 `rebuild-all` |

## 冷启动流程

1. 启动 Qdrant：**在 `D:\qdrant` 目录下运行 `qdrant.exe`**（cwd 错误会在仓库根生成 storage/ 垃圾目录——2026-08-16 踩过）
2. 首次使用或改规则后：`python scripts/rebuild_index.py --all`
3. 启动 API：`dotnet run`
4. **改规则文件后必须重启 API**——`GameRulesService.LoadJson` 按路径缓存 JSON 永不失效（2026-08-16 两次复测被坑）

日常重启只需第 1、3 步。向量索引持久化在 Qdrant 磁盘上，重启不丢失、不重建。

## Python 重建脚本（默认增量）

`scripts/rebuild_index.py`，完全离线：

```
D:\Python\Python312\python.exe scripts\rebuild_index.py --all              # 增量同步所有游戏
D:\Python\Python312\python.exe scripts\rebuild_index.py --game civolution  # 增量同步指定游戏
D:\Python\Python312\python.exe scripts\rebuild_index.py --all --full       # 强制全量（模型/提取逻辑大改时）
```

依赖：`onnxruntime` + `transformers` + `requests`（均已安装在 Python 3.12）。
读本地 ONNX 模型（`ml_models/bge-base-zh-v1.5-fp32/`），通过 Qdrant HTTP API 建 collection 并 upsert。

**增量机制（2026-08-18）**：point ID 由 `game::source::concept_id` 确定性生成，
payload 带 `content_hash`（概念 id/type/name/向量文本的 SHA256）与 `model_tag`。
同步时 scroll 现有 points → diff → 只对新增/变化点 embedding 并 upsert，
消失点按 ID 删除；collection 缺失、维度不匹配或 `--full` 时回退全量重建。
Python 与 C# 两条路径实现同一套 diff 逻辑：
`scripts/rebuild_index.py`（默认增量，`--full` 强制全量）与
C# 端 `VectorSearchService.SyncIndexAsync`（`dotnet run --rebuild-all/--rebuild-index` 默认增量，
`--full` 强制全量；`VectorSearchService.RebuildIndexAsync` 为全量实现，
但会写同款 `content_hash`/`model_tag` payload，保证 Python 增量可以接着 C# 重建结果继续 diff）。
首次从旧索引迁移时（无 content_hash 或旧 C# point ID 端序），会自动重算该游戏全部点一次；
迁移完成后即恢复为真正的点级增量。

## 相关记忆

- [[runtime-architecture]] — 后端接口与工具设计
- [[project-overview]] — 项目阶段与当前重点
- [[ontology-design]] — 本体扩展约定
- [[civolution-progress]] — 第二款游戏的形式化进度

**Why:** 向量检索是搜索质量的关键升级。记录了完整的部署与使用方式，避免后续重新推导。
**How to apply:** 修改规则文件后跑 `python scripts/rebuild_index.py --game <game>` 增量同步索引（默认）；日常启动只需 Qdrant + dotnet run。
