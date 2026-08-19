# BoardAI.Api

桌游规则 AI 的最小后端服务。目前包含两个部分：

1. **Chat 接口**：接收客人文字，转发给 LLM，返回回答。
2. **Rules 接口**：读取 `ontology/` 和 `games/splendor/` 下的 JSON 规则文件，按概念 ID / 类型 / 关键词返回结构化信息。

## 运行

本项目使用 .NET 9 SDK，安装位置：`D:\dotnet`

直接双击项目目录下的 `start.bat`，或在终端执行：

```bash
cd backend/BoardAI.Api
D:\dotnet\dotnet.exe run --urls "http://localhost:5000"
```

默认监听 `http://localhost:5000`。

> 如果你已经在系统 PATH 里加了 `D:\dotnet`，也可以直接用 `dotnet run`。

### WSL / Linux（迁移中推荐）

仓库根目录已提供 WSL 启动脚本（Linux 本地 .NET SDK + Linux 本地 Qdrant；若没有 Linux Qdrant，也会自动回退到 Windows 的 `D:\qdrant\qdrant.exe`）：

```bash
# 启动 Qdrant（已运行会自动跳过）
./scripts/start_qdrant.sh

# 启动 API（会自动 build，并监听 http://localhost:5000）
./scripts/start_api.sh

# 重建索引（替代 Python 版）
./scripts/rebuild_index.sh --game splendor
./scripts/rebuild_index.sh --all
```

当前 WSL 环境使用 `backend/BoardAI.Api/ml_models/bge-base-zh-v1.5-fp32` 作为默认模型，配置已在 `appsettings.json` 中改为相对路径。脚本里的本地 .NET SDK 是 10.x，通过 `DOTNET_ROLL_FORWARD=Major` 运行 `net9.0` 目标，无需额外安装 .NET 9 运行时。

Embedding 已启用 GPU 优先：使用 `Microsoft.ML.OnnxRuntime.Gpu` 和 `CUDAExecutionProvider`；如果当前环境没有可用 GPU，会自动回退 CPU。

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

## 项目结构

- `Controllers/ChatController.cs`：Chat HTTP 入口
- `Controllers/RulesController.cs`：规则查询 HTTP 入口
- `Services/ILLMService.cs`：LLM 抽象
- `Services/DeepSeekLLMService.cs`：DeepSeek v4 Pro 实现
- `Services/GameRulesService.cs`：读取 ontology / game JSON 规则
- `Models/`：请求/响应/配置模型

## 后续扩展方向

1. 在 `ILLMService` 之上加入工具调用循环（Tool Use / ReAct）。
2. 让 LLM 能够调用规则查询接口，再组织自然语言回答。
3. 增加意图分类层，把客人问题路由到不同处理流程。
