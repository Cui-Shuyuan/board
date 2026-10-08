# BoardAI.Api

桌游规则 AI Runtime 的后端服务，对外提供四组能力：

1. **Chat 接口**：接收 `game_id` 和完整 `messages`（可带 Android 播放 context），LLM 生成 `execute_plan`，服务端执行规则查询并返回 TTS 友好回答。
2. **Rules 接口**：读取 `content/ontology/` 与 `content/games/` 下的结构化规则，提供类型 / 概念 / 检索 / action 条件 / 查询计划 / 索引重建。
3. **Catalog / Content 接口**：提供 Android 首页游戏目录，以及 manifest 驱动的版本化内容下载、缓存与断点续传。
4. **ASR / TTS 接口**：通过 `tools/voice/` 下的 Python 短进程桥接火山引擎，Android 不接触服务密钥。

## 运行

本项目使用 .NET 9 SDK。

直接双击项目目录下的 `start.bat`，或在终端执行：

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

默认监听 `http://localhost:5000`；`start.bat` 可通过 `DOTNET_EXE`、`BOARD_API_URLS`、`QDRANT_EXE` 等环境变量覆盖本机工具路径。

## 可移植路径配置

`appsettings.json` 不保存机器绝对路径；服务端统一通过 `Infrastructure/BoardPaths.cs` 解析：

1. 优先读取环境变量 `BOARD_BASE_PATH`；
2. 否则从 `AppContext.BaseDirectory` 向上查找同时包含 `content/games` 和 `backend` 的仓库根；
3. 否则回退到 `Directory.GetCurrentDirectory()`。

然后：

- `Rules:BasePath` 是可选的显式覆盖；为空时使用上面的仓库根。相对路径按仓库根解析。
- `Embedding:ModelDir` 为空时默认使用 `{仓库根}/backend/BoardAI.Api/ml_models/bge-base-zh-v1.5-fp32`；相对路径按仓库根解析，绝对路径原样使用。
- `ContentController`、`Program.cs`、`GameRulesService` 共用同一套解析逻辑。

示例：

```bash
set BOARD_BASE_PATH=D:\path\to\board
set Embedding__ModelDir=D:\models\bge-base-zh-v1.5-fp32
dotnet run --urls "http://0.0.0.0:5000"
```

## 配置 LLM

`appsettings.json` 只保存 Provider / BaseUrl / Model / SystemPrompt，`LLM:ApiKey` 保持空串；密钥只走环境变量（推荐，避免把 key 提交到仓库）：

```bash
set LLM__ApiKey=sk-xxxxxxxx
:: 兼容旧变量名
set DEEPSEEK_API_KEY=sk-xxxxxxxx
```

当前模型为 `deepseek-v4-flash`，以 `appsettings.json` 为准。

## 调用示例

### Chat 接口

```bash
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"game_id":"splendor","messages":[{"role":"user","content":"贵族怎么获得？"}]}'
```

返回：

```json
{
  "reply": "..."
}
```

Android 教程播放器还会在请求里带可选 `context`（`cue_id` / `cue_index` / `cue_text` / `group_path` / `recent_cues` 等），让 LLM 结合当前讲规进度回答；普通规则问答省略即可。

### Rules 接口

主要路由：

```text
GET  /api/rules/games
GET  /api/rules/games/{game}/types
GET  /api/rules/games/{game}/concepts?type=...
GET  /api/rules/games/{game}/concepts/{id}
GET  /api/rules/games/{game}/actions/{actionId}/conditions
GET  /api/rules/games/{game}/search?q=...
POST /api/rules/games/{game}/execute-plan
POST /api/rules/admin/rebuild-index/{game}
POST /api/rules/admin/rebuild-all
```

列出所有概念类型：

```bash
curl -s "http://localhost:5000/api/rules/games/splendor/types"
```

列出所有 action：

```bash
curl -s "http://localhost:5000/api/rules/games/splendor/concepts?type=actions"
```

查询某个 action 的定义：

```bash
curl -s "http://localhost:5000/api/rules/games/splendor/concepts/take_gems_same"
```

查询某个 action 的所有条件：

```bash
curl -s "http://localhost:5000/api/rules/games/splendor/actions/take_gems_same/conditions"
```

按关键词搜索概念：

```bash
curl -s "http://localhost:5000/api/rules/games/splendor/search?q=%E8%B4%B5%E6%97%8F"
```

## 内容接口缓存

内容接口：

```text
GET /api/content/games/{game}/manifest
GET /api/content/games/{game}/files/{version}/{**filePath}
GET /api/content/games/{game}/files/{**filePath}   # 兼容旧 URL，未来可删
```

- `manifest` 与旧文件 URL 返回 `Cache-Control: no-cache, must-revalidate` + ETag，支持 `If-None-Match` 304。
- `files/{version}/...` 是 versioned URL：version 必须与当前 manifest.version 一致，否则返回 `409 Conflict` 并提示重新拉 manifest。文件从 `content/releases/{game}/{version}/` 不可变目录读取，缺文件返回 404，绝不回退到可修改的 `content/games/{game}`。成功返回 `Cache-Control: public, max-age=31536000, immutable` + ETag，可长期缓存/接 CDN。
- 旧 `/files/{**filePath}` 路由保留给旧客户端兼容，仍从 `content/games` 实时读取，继续使用 `no-cache, must-revalidate`，不提供不可变承诺，后续版本可删除。

`GET /api/catalog/games` 在 catalog JSON 基础上补齐能力字段：

- `rules_ready` / `tutorial_ready`：规则问答与教程是否可用；
- `tutorial_tracks`：可播放 track 列表（例如 `["full"]`）；
- 旧字段 `tutorial_track` 仅作兼容；只有 `rules_ready=true` 且 `tutorial_ready=false` 的规则-only 游戏不会暴露教程下载元数据。

## 项目结构

- `Controllers/ChatController.cs`：Chat HTTP 入口
- `Controllers/RulesController.cs`：规则查询 / `execute_plan` / 索引重建 HTTP 入口
- `Controllers/CatalogController.cs`：Android 首页游戏目录
- `Controllers/ContentController.cs`：manifest / 内容文件只读接口
- `Controllers/AsrController.cs` / `Controllers/TtsController.cs`：语音接口
- `Infrastructure/BoardPaths.cs`：可移植仓库路径解析
- `Services/ILLMService.cs` / `Services/OpenAICompatibleLLMService.cs` / `Services/DeepSeekLLMService.cs`：LLM 抽象与实现
- `Services/GameRulesService.cs`：薄 facade；业务拆为 `RulesContentStore` / `RulesConceptCatalog` / `RulesNameIndexService` / `RulesSearchService` / `RulesIndexService` / `RulesFlowService` / `RulesReferenceService` / `RulesFactService` / `RulesPlanService`
- `Services/ChatOrchestratorService.cs`：查询计划循环与回答组织
- `Services/VectorSearchService.cs` / `Services/EmbeddingService.cs`：Qdrant / ONNX 检索
- `Services/AsrService.cs` / `Services/TtsService.cs` / `Services/VoiceProcessRunner.cs`：语音桥
- `Models/`：请求/响应/配置模型

## 后续扩展方向

1. 按 `.claude/memory/current-state.md` 推进：Splendor full 逐 cue 重审、其余 8 款游戏 catalog / manifest。
2. Flow Guide（下一产品方向）：程序维护流程游标，条件判断交玩家回答；首个目标是 Civolution 顶层时代/阶段循环与终局计分助手。
3. 语音 / Android 真机端到端验收，以及问答打断后回到动画的完整链路复测。
