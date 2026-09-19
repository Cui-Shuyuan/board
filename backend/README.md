# BoardAI 后端（规则问答引擎）

    D:\dotnet\dotnet.exe run --project D:\workspace\board\backend\BoardAI.Api --urls http://0.0.0.0:5000

- 只用 `localhost` 时 WSL 访问不到（防火墙），要在 WSL 里问就绑 `0.0.0.0` 并用宿主 IP；
- **API key 只走环境变量，绝不写进仓库**：

      setx DEEPSEEK_API_KEY "sk-…"      :: 用户级，设完要新开终端/IDE 才生效
      :: 也认 ASP.NET 约定：LLM__ApiKey

  `appsettings.json` 里 `LLM:ApiKey` 保持空串；代码已支持环境变量兜底
  （见 `Services/DeepSeekLLMService.cs`）。
- 历史教训：这个 key 从 2026-07-23 起被写在 `appsettings.json` 里并推到了 GitHub，
  只能轮换（已处置）。防再犯：`python3 scripts/check_no_secrets.py`（扫已跟踪文件里的明文密钥），
  也可以挂成钩子：

      ln -sf ../../scripts/check_no_secrets.py .git/hooks/pre-commit   # 或写个两行 wrapper

- 从 WSL 启动 Windows 侧服务时，**WSL 的环境变量不会自动传过去**：
  用 `cmd.exe /c "set DEEPSEEK_API_KEY=…&& D:\dotnet\dotnet.exe run …"`，
  否则服务读到空 key → `401 Authorization Required`。
