# embedding （确保qdrant进程正在运行）

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

# 语音服务配置（标准 TTS + 一句话 ASR）

当前默认使用火山引擎**标准语音合成（小模型 v1 WebSocket）**，不再默认使用
`seed-tts-2.0` 双向流式接口。仓库根目录 `.env` 建议配置：

```dotenv
VOLCENGINE_API_KEY=...
DOUBAO_TTS_PROVIDER=standard
DOUBAO_TTS_VOICE=BV700_streaming
DOUBAO_TTS_CLUSTER=volcano_tts
DOUBAO_TTS_ENDPOINT=wss://openspeech.bytedance.com/api/v1/tts/ws_binary
DOUBAO_TTS_RESOURCE_ID=volc.tts.default

VOLCENGINE_ASR_ENDPOINT=wss://openspeech.bytedance.com/api/v2/asr
# 如果共享 API Key 无法用于旧版一句话小模型 v2 接口，再配置旧版三件套：
# VOLCENGINE_ASR_AUTH=legacy
# VOLCENGINE_ASR_APP_ID=...
# VOLCENGINE_ASR_ACCESS_TOKEN=...
# VOLCENGINE_ASR_CLUSTER=...
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
