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
GET /api/content/games/{game}/files/{**filePath}   # 无版本兼容 URL
```

- manifest 读取 `content/manifests/{game}.json`，文件不存在返回 404 和明确 message；每次请求重新读文件，并生成基于 `Length + LastWriteTimeUtc.Ticks` 的 ETag，支持 `If-None-Match`。
- runtime-only 包由 `tools/content/build_content_manifest.py` 收集 `tutorial/{track}.runtime.json`、`tutorial/anim/v2/{track}.compiled.json` 及其实际引用的音频/图片/字幕，发布到 `content/releases/{game}/{version}/`；QA 日志、动画源、stage 源、文档、脚本、`.pyc`、`.lrc` 不会进入 version。
- versioned 文件接口从 `content/releases/{game}/{version}/{filePath}` 流式返回，支持 Range，按扩展名设置 Content-Type。请求 version 与当前 manifest.version 不一致时返回 `409 Conflict`；匹配时返回 `Cache-Control: public, max-age=31536000, immutable` + ETag；release 缺文件返回 404，不回退到可修改的 `content/games`。
- 无版本的 `/files/{**filePath}` 路由保留兼容，从 `content/games/{game}/{filePath}` 返回 `Cache-Control: no-cache, must-revalidate`，不参与不可变承诺。
- 路径安全：拒绝绝对路径、`..` / `.` 段、编码的 `%2e` / `%2f` / `%5c`，并用 `Path.GetFullPath` + game 根目录前缀做第二层校验；非法路径返回 400。
- 内容文件/ manifest 更新后无需重启 API。

生成 manifest 与不可变 release：

```bash
cd <repo-root>
python3 tools/content/build_content_manifest.py --game splendor
```

输出 `content/manifests/splendor.json`（生成物不入 Git）和 `content/releases/splendor/{version}/`（生成目录不入 Git）。version 由 package 内文件的 `path + sha256` 排序拼接后再取 SHA-256 前 16 位，只由 runtime package 决定。发布使用 `{version}.tmp` 暂存、逐文件校验、原子 rename，最后以 manifest `.tmp + atomic rename` 切换当前指针；同 version 已存在但字节不一致时明确失败，不会覆盖。服务端保留当前 release + 最近 2 个历史 release，回滚可通过恢复旧源文件后重跑 builder 复用已校验 release 完成。

Catalog 能力字段：

- `rules_ready`：有可问答规则数据。
- `tutorial_ready`：有完整教程 runtime 包；补 manifest 不等于教程就绪。
- `tutorial_tracks`：可播放 track 列表，例如 `["full"]`；无教程时为空。
