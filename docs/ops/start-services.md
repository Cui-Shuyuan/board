# 本地服务启动与内容发布

仓库路径示例：Windows `D:\workspace\board`；WSL `/mnt/d/workspace/board`。以下命令以仓库根为工作目录。

## 1. Qdrant

启动 Windows 原生 Qdrant（建议在 Qdrant 自身目录启动，避免在仓库根生成 storage）：

```powershell
cd D:\qdrant
qdrant.exe
```

- HTTP `6333` 给 Python 索引脚本；gRPC `6334` 给 .NET API。
- Python CLI 默认连 `http://localhost:6333`，可用 `QDRANT_URL` 指向其他实例。

## 2. 重建向量索引

```bash
# 单款
python3 tools/indexing/rebuild_index.py --game splendor
# 全部九款
python3 tools/indexing/rebuild_index.py --all
# 只构建、不切换 alias
python3 tools/indexing/rebuild_index.py --all --no-switch
```

后端等价入口：`POST /api/rules/admin/rebuild-index/{game}`、`POST /api/rules/admin/rebuild-all`。规则 JSON 改文件由 `RulesDocumentStore` 自动 freshness；语义检索必须重建索引。

## 3. 启动 BoardAI.Api

```bash
cd backend/BoardAI.Api
dotnet run --urls "http://0.0.0.0:5000"
```

API key 只走环境变量：

```bash
export LLM__ApiKey="sk-..."
# 兼容旧变量名：DEEPSEEK_API_KEY
```

Windows 下同样可用 `set LLM__ApiKey=...`。

验证：

```bash
curl -s http://localhost:5000/api/rules/games/splendor/types
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"game_id":"splendor","messages":[{"role":"user","content":"贵族怎么获得？"}]}'
```

## 4. 内容发布（不可变 manifest / release）

`content/manifests/*.json` 与 `content/releases/` 是生成物，不入 Git；`tools/ops/sync_workspaces.sh` 只搬 Git 提交，不会同步这两个目录。

生产 API 主机在内容源更新后必须执行：

```bash
python3 tools/content/build_content_manifest.py --game <game_name>
```

生成：

- `content/manifests/{game}.json`
- `content/releases/{game}/{version}/`

版本化接口只读取 release 目录；只同步代码不会让客户端拿到新内容。若生产机不方便运行构建，必须把已校验的 manifest 和 release 目录单独部署到 API 仓库根目录下。

## 5. 语音服务配置

语音服务默认使用火山引擎**标准语音合成（小模型 v1 WebSocket）**；需要 `seed-tts-2.0` 双向流式接口时设置 `DOUBAO_TTS_PROVIDER=seed2`。

ASR 默认在 `BoardAI.Api` 进程内直连火山，不再启动 Python 子进程；只有显式设置 `Voice__AsrMode=python`（或 `appsettings` 中 `Voice:AsrMode=python`）时才回退 `tools/voice/asr_once.py`。

仓库根 `.env` 建议：

```dotenv
VOLCENGINE_API_KEY=...
DOUBAO_TTS_PROVIDER=standard
DOUBAO_TTS_VOICE=BV700_streaming
DOUBAO_TTS_CLUSTER=volcano_tts
DOUBAO_TTS_ENDPOINT=wss://openspeech.bytedance.com/api/v1/tts/ws_binary
DOUBAO_TTS_RESOURCE_ID=volc.tts.default

VOLCENGINE_ASR_ENDPOINT=wss://openspeech.bytedance.com/api/v2/asr
# 一句话小模型使用 AppID/AccessToken + cluster=volcengine_input：
# VOLCENGINE_ASR_APP_ID=...         或 VOLCENGINE_APP_ID
# VOLCENGINE_ASR_ACCESS_TOKEN=...   或 VOLCENGINE_ACCESS_TOKEN
# VOLCENGINE_ASR_CLUSTER=volcengine_input  或 VOLCENGINE_CLUSTER
```

切回语音合成 2.0：

```dotenv
DOUBAO_TTS_PROVIDER=seed2
DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts
DOUBAO_RESOURCE_ID=seed-tts-2.0
```

自检：

```bash
PYTHONPATH=. python3 tools/voice/tts_once.py \
  --text "这是一次语音测试" \
  --out-file /tmp/tts-test.mp3

curl -X POST http://localhost:5000/api/tts \
  -H "Content-Type: application/json" \
  -d '{"text":"这是一次语音测试"}' \
  --output /tmp/backend-tts.mp3

# ASR：16k/16bit/单声道 WAV
curl -X POST http://localhost:5000/api/asr/once \
  -H "Content-Type: audio/wav" \
  --data-binary @/path/to/16k-mono.wav
```

## 6. Android 真机验收（由用户执行）

1. 启动 Qdrant 与 `BoardAI.Api`，确认手机能访问 `BOARD_API_BASE_URL`。
2. 打开教程问答面板，按住“按住说话”录入一句中文，松开。
3. 确认识别文本回填到输入框、失败时面板不崩溃、不覆盖原有文字答案流程。
4. 发送问题后应自动播放回答音频；点“重播”可再次播放，点“继续播放”会先停止 TTS。
5. 进入问答前若动画正在播放，打开面板时先暂停；Unity 回传暂停状态后，Android 记录当前 `cueId + cue 内位置`。点“继续播放”时发送 `PlayCueAt`，精确恢复到进入问答时的位置，而不是从头重播当前小节。
6. 进入问答前若动画本来就处于暂停/未播放状态，关闭面板只回到对应界面，不自动播放；有未完成 ASR/TTS/chat 请求时先取消，再恢复。
7. 连续 PTT、切换会话、关闭面板/返回首页交错时，迟到 ASR/TTS/chat 结果必须被 generation 丢弃；回答音频在会话结束或关闭面板时清理。
8. 真机端到端完成后，再关闭“完整真机验收待复测”缺口。
