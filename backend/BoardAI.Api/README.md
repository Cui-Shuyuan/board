# BoardAI.Api

桌游规则 AI 的最小后端服务。目前只负责一件事：接收客人的文字输入，原封不动转发给 LLM，再把 LLM 回答返回给客人。

## 运行

```bash
cd backend/BoardAI.Api
dotnet run
```

默认监听 `http://localhost:5000`。

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

```bash
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"message": " Splendor 怎么玩？"}'
```

返回：

```json
{
  "reply": "..."
}
```

## 项目结构

- `Controllers/ChatController.cs`：HTTP 入口
- `Services/ILLMService.cs`：LLM 抽象
- `Services/DeepSeekLLMService.cs`：DeepSeek v4 Pro（OpenAI 兼容协议）实现
- `Models/`：请求/响应/配置模型

## 后续扩展方向

1. 在 `ILLMService` 之上加入工具调用循环（Tool Use / ReAct）。
2. 让 LLM 能够调用规则查询接口，再组织自然语言回答。
3. 增加意图分类层，把客人问题路由到不同处理流程。
