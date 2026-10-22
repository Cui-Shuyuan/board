# 代码仓库与运行时路径分离

## 目标

Git 仓库只保留：

- 源代码、测试、工具、动画生产链和文档；
- 规则/内容源真值（`content/ontology`、`content/games/*/concepts/flow/instances`、tutorial source、catalog）；
- 内容的 source media（如果它是内容生产链的输入，属于数据而非可重建缓存）。

以下数据不再默认写入 checkout：

- .NET bin/obj/artifacts；
- 模型、Qdrant 数据、规则搜索磁盘缓存；
- content manifest 与 immutable release；
- Unity `Library/Builds/Logs/CaptureOut`；
- Android Gradle/build 输出；
- 运行凭证、日志、临时目录、本地规则书和扫描件。

本机运行时根目录：

```text
D:\board        (WSL: /mnt/d/board)
```

由环境变量 `BOARD_RUNTIME_ROOT` 指定。

## 迁移后布局

```text
D:\board\
  build\dotnet\                  .NET artifacts（UseArtifactsOutput）
  models\bge-base-zh-v1.5-fp32\ 本地 ONNX/tokenizer
  qdrant\                       Qdrant 数据、空快照目录并归档旧 API storage
  cache\rules-derived-cache\    规则搜索材料磁盘缓存
  content\manifests\            manifest 生成物
  content\releases\             不可变内容发布目录
  clients\unity\                Library/Builds/Logs/CaptureOut/UserSettings/utmp
  clients\android\              .gradle/build/app-build 等生成物
  config\.env                   运行凭证（已从仓库根移出）
  logs\                         运行日志
  local-doc\                    规则书、扫描页、历史研究材料
  tmp\                          scratch / 历史迁移产物
```

## 配置入口

- `BOARD_RUNTIME_ROOT`：运行时根目录。
- `Rules:DiskCache:Directory`：为空时使用 `$BOARD_RUNTIME_ROOT/cache/rules-derived-cache`。
- `Content:ManifestDirectory` / `Content:ReleaseDirectory`：为空时使用 runtime content 目录。
- `Embedding:ModelDir`：为空时使用 `$BOARD_RUNTIME_ROOT/models/bge-base-zh-v1.5-fp32`。
- `BOARD_RUNTIME_ROOT/config/.env`：优先读取；旧仓库根 `.env` 仅作兼容回退。
- `Directory.Build.props` + git-ignored `runtime.local.props`：把 .NET 输出重定向到 `D:\board\build\dotnet`。
- `backend/BoardAI.Api/start.bat` 和 `tools/ops/start_api.sh` 会设置上述 runtime 环境变量。

`.gitignore` 中的 bin/obj、storage、media 等规则保留作为防护，避免误用默认输出路径时把生成物提交进 Git；不代表它们仍应位于仓库内。

## 本次实际迁移

已从 checkout 移出（路径明细可在 `/mnt/d/board/README.md` 查看）：

- `.tmp`、本地 `doc/`、仓库 `archive/`、临时 TTS 文件、API 日志；
- .NET API/测试/diagnostics 的 bin/obj，Unity Library/Builds/Logs 等，Android Gradle/build；
- `backend/BoardAI.Api/storage`、Qdrant 初始化标记；
- `content/manifests`、`content/releases`；
- 本地模型，以及规则书/扫描页；
- 仓库根 `.env`（移到 `D:\board\config\.env`）。

迁移后 checkout 从约 36 GB 降到约 220 MB（包含 `.git` 与仍保留的 content source media）。

## 验证

- .NET 构建输出：`D:\board\build\dotnet\bin\...`。
- Release 后端测试：285/285。
- Content manifest 测试：15/15。
- `python3 tools/content/build_content_manifest.py --game splendor` 成功写入
  `D:\board\content\manifests` 与 `D:\board\content\releases`。
- 重启 API 后：
  - `GET /api/rules/cache/stats` 返回 200，disk directory 为
    `D:\board\cache\rules-derived-cache`；
  - `GET /api/content/games/splendor/manifest` 返回 200；
  - versioned content file 返回 200；
  - 两次 `search?q=贵族` 结果一致，第二次命中内存缓存。

## 2026-10-23 cleanup

- `D:\board\tmp` was reduced from about 11 GB to empty. Build caches, old
  worktrees, duplicate model copies, Gradle homes, device packages, TTS probes,
  screenshots and old checkout copies were deleted because they are rebuildable
  or reproducible from Git.
- Historical raw CA/eval outputs referenced by review reports were compressed to
  `D:\board\archive\agent-workspace-evidence-20261023-004025.tar.zst`
  (about 321 MB) rather than silently discarded. If historical raw evidence is
  no longer needed, that single archive can be deleted.
- Legacy `D:\board\build\dotnet\api-*`/diagnostics/test output directories
  were removed; only `build/dotnet/bin` and `build/dotnet/obj` remain for the
  current SDK artifacts layout.
