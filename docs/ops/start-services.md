# embedding （确保qdrant进程正在运行）

CLI 与后端 `POST /api/rules/admin/rebuild-index/{game}` 共用同一索引契约：
先写 `board_{game}__v{version}` / `...__name` 两个具体 collection，校验点数后
原子切换 `board_{game}__active` / `..._active_name` 别名；失败保留旧索引。
Python CLI 默认连 `http://localhost:6333`，可用环境变量指向其他实例：

```bash
QDRANT_URL=http://<host>:6333 python3 tools/indexing/rebuild_index.py --game <game_name>
```

## 单个游戏
D:\Python\Python312\python.exe D:\workspace\board\tools\indexing\rebuild_index.py --game <game_name>

## 全部游戏
D:\Python\Python312\python.exe D:\workspace\board\tools\indexing\rebuild_index.py --all

# 启动服务

## 启动 Qdrant
D:\qdrant\qdrant.exe

## 启动 API
（在 D:\workspace\board\backend\BoardAI.Api 路径下）
dotnet run

或者 dotnet run --rebuild-index <game_name>

# 内容发布（不可变 manifest / release）

`content/manifests/*.json` 和 `content/releases/` 是生成物，不入 Git；
`tools/ops/sync_workspaces.sh` 只搬 Git 提交，不会同步这两个目录。

生产 API 主机在内容源更新后必须执行：

```bash
python3 tools/content/build_content_manifest.py --game <game_name>
```

该命令生成 `content/manifests/{game}.json` 与 `content/releases/{game}/{version}/`，
版本化接口只读取 release 目录。若生产机不方便运行构建，则必须把已校验的
`content/manifests/{game}.json` 和对应的 `content/releases/{game}/{version}/`
单独部署到 API 仓库根目录下；只同步代码不会让客户端拿到新内容。

# 语音服务配置（标准 TTS + 一句话 ASR）

语音服务默认使用火山引擎**标准语音合成（小模型 v1 WebSocket）**；
需要 `seed-tts-2.0` 双向流式接口时设置 `DOUBAO_TTS_PROVIDER=seed2`。
仓库根目录 `.env` 建议配置：

```dotenv
VOLCENGINE_API_KEY=...
DOUBAO_TTS_PROVIDER=standard
DOUBAO_TTS_VOICE=BV700_streaming
DOUBAO_TTS_CLUSTER=volcano_tts
DOUBAO_TTS_ENDPOINT=wss://openspeech.bytedance.com/api/v1/tts/ws_binary
DOUBAO_TTS_RESOURCE_ID=volc.tts.default

VOLCENGINE_ASR_ENDPOINT=wss://openspeech.bytedance.com/api/v2/asr
# v2 一句话小模型可能不接受共享 API Key（403 resource not granted）；
# 三件套齐全时 asr_once.py 自动优先用 console token，无需额外设置。
# 想强制试共享 API Key 再设 VOLCENGINE_ASR_AUTH=apikey。
# 一句话小模型使用 AppID/AccessToken + cluster=volcengine_input：
# VOLCENGINE_ASR_APP_ID=...   # 或 VOLCENGINE_APP_ID
# VOLCENGINE_ASR_ACCESS_TOKEN=...  # 或 VOLCENGINE_ACCESS_TOKEN
# VOLCENGINE_ASR_CLUSTER=volcengine_input  # 或 VOLCENGINE_CLUSTER；不是 TTS 的 volcano_tts，也不是 SECRET_KEY
```

切回语音合成 2.0：

```dotenv
DOUBAO_TTS_PROVIDER=seed2
DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts
DOUBAO_RESOURCE_ID=seed-tts-2.0
```

文本 TTS 自检（不依赖真人语音）：

```bash
PYTHONPATH=... python3 tools/voice/tts_once.py \
  --text "这是一次语音测试" \
  --out-file /tmp/tts-test.mp3
```

后端 `/api/tts` 自检：

```bash
curl -X POST http://localhost:5000/api/tts \
  -H "Content-Type: application/json" \
  -d '{"text":"这是一次语音测试"}' \
  --output /tmp/backend-tts.mp3
```

Android 真机验收（由用户执行）：

1. 启动 Qdrant 与 `BoardAI.Api`，确认手机能访问 `BOARD_API_BASE_URL`。
2. 打开教程问答面板，按住“按住说话”录入一句中文，松开。
3. 确认识别文本回填到输入框、失败时面板不崩溃、不覆盖原有文字答案流程。
4. 发送问题后应自动播放回答音频；点“重播”可再次播放，点“继续播放”会先停止 TTS。
