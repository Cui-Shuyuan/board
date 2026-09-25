# BoardAI.Api

桌游规则 AI 的最小后端服务。目前包含两个部分：

1. **Chat 接口**：接收客人文字，转发给 LLM，返回回答。
2. **Rules 接口**：读取 `content/ontology/` 和 `content/games/splendor/` 下的 JSON 规则文件，按概念 ID / 类型 / 关键词返回结构化信息。

## 运行

本项目使用 .NET 9 SDK。

直接双击项目目录下的 `start.bat`，或在终端执行：

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

默认监听 `http://localhost:5000`；`start.bat` 可通过 `DOTNET_EXE`、`BOARD_API_URLS`、`QDRANT_EXE` 等环境变量覆盖本机工具路径。

## 可移植路径配置

`appsettings.json` 不再保存机器绝对路径。服务端统一通过 `Infrastructure/BoardPaths.cs` 解析：

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

修改 `appsettings.json`：

```json
{
  "LLM": {
    "Provider": "DeepSeek",
    "BaseUrl": "https://api.deepseek.com/v1/",
    "Model": "deepseek-v4-pro",
    "ApiKey": "sk-xxxxxxxx"
  }
}
```

或通过环境变量（推荐，避免把 key 提交到仓库）：

```bash
set LLM__ApiKey=sk-xxxxxxxx
```

## 调用示例

### Chat 接口

```bash
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": "Splendor 怎么玩？"}'
```

返回：

```json
{
  "reply": "..."
}
```

### Rules 接口

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
- `files/{version}/...` 是 versioned URL：version 必须与当前 manifest.version 一致，否则返回 `409 Conflict` 并提示重新拉 manifest。成功返回 `Cache-Control: public, max-age=31536000, immutable` + ETag，可长期缓存/接 CDN。
- 旧 `/files/{**filePath}` 路由保留给旧客户端兼容，继续使用 `no-cache, must-revalidate`，后续版本可删除。

## 项目结构

- `Controllers/ChatController.cs`：Chat HTTP 入口
- `Controllers/RulesController.cs`：规则查询 HTTP 入口
- `Controllers/ContentController.cs`：manifest / 内容文件只读接口
- `Infrastructure/BoardPaths.cs`：可移植仓库路径解析
- `Services/ILLMService.cs`：LLM 抽象
- `Services/DeepSeekLLMService.cs`：DeepSeek v4 Pro 实现
- `Services/GameRulesService.cs`：读取 ontology / game JSON 规则
- `Models/`：请求/响应/配置模型

## 后续扩展方向

1. 在 `ILLMService` 之上加入工具调用循环（Tool Use / ReAct）。
2. 让 LLM 能够调用规则查询接口，再组织自然语言回答。
3. 增加意图分类层，把客人问题路由到不同处理流程。
