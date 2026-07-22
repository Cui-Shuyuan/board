---
name: vector-search
description: 向量语义检索架构——Qdrant + BGE-small-zh ONNX 的部署与使用方式
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
| Embedding 模型 | ONNX 文件，C# 推理 | `ml_models/bge-small-zh/` |
| 重建索引脚本 | Python，不依赖 .NET | `scripts/rebuild_index.py` |

## Qdrant

- 版本 1.18.3，Windows 原生二进制，单文件
- gRPC 端口 6334（.NET 客户端），HTTP 端口 6333（Python 脚本用）
- 数据持久化：`D:\qdrant\data\`，配置通过环境变量 `QDRANT__STORAGE__STORAGE_PATH`
- **每个游戏一个 collection**：`board_{gameId}`（如 `board_civolution`、`board_splendor`）
- 每个 collection 里存该游戏的全部概念向量（含 ontology 概念副本）

## Embedding 模型

- BGE-small-zh (BAAI)，512 维，L2 归一化
- 从 PyTorch 导出为 ONNX（`model.onnx` + `model.onnx.data`，合计 ~92MB）
- 导出命令：`optimum-cli export onnx --model BAAI/bge-small-zh --task sentence-similarity <output_dir>`
- 需要 Python 3.12（D 盘），`optimum-onnx[onnxruntime]` + `sentence-transformers` + `onnxscript`
- 分词器：`tokenizer.json` + `vocab.txt`，C# 端用 `Tokenizers.HuggingFace` NuGet 加载，Python 端用 `transformers.AutoTokenizer`

## 索引内容

每款游戏的索引覆盖：
- `ontology/ontology.json` → concepts 数组
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

1. 启动 Qdrant：`D:\qdrant\qdrant.exe`（或 `start.bat` 自动）
2. 首次使用或改规则后：`python scripts/rebuild_index.py --all`
3. 启动 API：`dotnet run`

日常重启只需第 1、3 步。向量索引持久化在 Qdrant 磁盘上，重启不丢失、不重建。

## Python 重建脚本

`scripts/rebuild_index.py`，完全离线：

```
D:\Python\Python312\python.exe scripts\rebuild_index.py --all
D:\Python\Python312\python.exe scripts\rebuild_index.py --game civolution
```

依赖：`onnxruntime` + `transformers` + `requests`（均已安装在 Python 3.12）。
读本地 ONNX 模型（`ml_models/bge-small-zh/`），通过 Qdrant HTTP API 建 collection 并 upsert。

## 相关记忆

- [[runtime-architecture]] — 后端接口与工具设计
- [[project-overview]] — 项目阶段与当前重点
- [[ontology-design]] — 本体扩展约定
- [[civolution-progress]] — 第二款游戏的形式化进度

**Why:** 向量检索是搜索质量的关键升级。记录了完整的部署与使用方式，避免后续重新推导。
**How to apply:** 修改规则文件后跑 `python scripts/rebuild_index.py --game <game>` 重建索引；日常启动只需 Qdrant + dotnet run。
