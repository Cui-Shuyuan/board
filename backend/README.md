# BoardAI 后端（规则问答引擎）

    dotnet run --project backend/BoardAI.Api --urls http://0.0.0.0:5000

- 只用 `localhost` 时 WSL 访问不到（防火墙），要在 WSL 里问就绑 `0.0.0.0` 并用宿主 IP；
- **API key 只走环境变量，绝不写进仓库**：

      setx DEEPSEEK_API_KEY "sk-…"      :: 用户级，设完要新开终端/IDE 才生效
      :: 也认 ASP.NET 约定：LLM__ApiKey

  `appsettings.json` 里 `LLM:ApiKey` 保持空串；代码已支持环境变量兜底
  （见 `Services/DeepSeekLLMService.cs`）。
- 历史教训：这个 key 从 2026-07-23 起被写在 `appsettings.json` 里并推到了 GitHub，
  只能轮换（已处置）。防再犯：`python3 tools/ops/check_no_secrets.py`（扫已跟踪文件里的明文密钥），
  也可以挂成钩子：

      ln -sf ../../tools/ops/check_no_secrets.py .git/hooks/pre-commit   # 或写个两行 wrapper

- 从 WSL 启动 Windows 侧服务时，**WSL 的环境变量不会自动传过去**：
  用 `cmd.exe /c "set DEEPSEEK_API_KEY=…&& dotnet run …"`，
  否则服务读到空 key → `401 Authorization Required`。

## 内容 manifest / 文件接口（v1）

Android 内容更新 v1 使用两个只读接口，不经过 Qdrant：

```text
GET /api/content/games/{game}/manifest
GET /api/content/games/{game}/files/{version}/{**filePath}
GET /api/content/games/{game}/files/{**filePath}   # 兼容旧 URL
```

- manifest 读取 `content/manifests/{game}.json`，文件不存在返回 404 和明确 message；每次请求重新读文件，并生成基于 `Length + LastWriteTimeUtc.Ticks` 的 ETag，支持 `If-None-Match`。
- versioned 文件接口从 `content/games/{game}/{filePath}` 流式返回，支持 Range，按扩展名设置 Content-Type。请求 version 与当前 manifest.version 不一致时返回 `409 Conflict`；匹配时返回 `Cache-Control: public, max-age=31536000, immutable` + ETag。
- 旧 `/files/{**filePath}` 路由是兼容路径，继续返回 `Cache-Control: no-cache, must-revalidate`，后续版本可删。
- 路径安全：拒绝绝对路径、`..` / `.` 段、编码的 `%2e` / `%2f` / `%5c`，并用 `Path.GetFullPath` + game 根目录前缀做第二层校验；非法路径返回 400。
- 内容文件/ manifest 更新后无需重启 API。

生成 manifest：

```bash
cd <repo-root>
python3 tools/content/build_content_manifest.py --game splendor
```

输出 `content/manifests/splendor.json`（生成物不入 Git）。version 由所有文件的 `path + sha256` 排序拼接后再取 SHA-256 前 16 位，只由内容决定。
