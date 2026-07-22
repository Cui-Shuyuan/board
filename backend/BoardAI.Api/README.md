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
