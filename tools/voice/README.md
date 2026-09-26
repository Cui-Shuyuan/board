# Voice bridge (ASR + TTS)

This directory contains the v1 Python bridge used by `BoardAI.Api`. The API
starts these scripts as short-lived child processes; Android never talks to
Volcengine directly and no key is shipped in the APK.

## Dependencies

```bash
pip install -r animation/requirements-tts.txt
```

`websockets` is used by both scripts. `mutagen` is used by the TTS bridge to
report mp3 duration.

## Recommended `.env`

Keep credentials in the repository-root `.env` (git-ignored) or export them
in the shell before starting the API. The new-console project-scoped API key
is shared by the enabled speech services; do not create separate ASR/TTS keys.

```dotenv
# Shared new-console API key
VOLCENGINE_API_KEY=

# TTS: standard small-model is the default
DOUBAO_TTS_PROVIDER=standard
DOUBAO_TTS_VOICE=BV700_streaming
DOUBAO_TTS_CLUSTER=volcano_tts
DOUBAO_TTS_ENDPOINT=wss://openspeech.bytedance.com/api/v1/tts/ws_binary
# Usage/billing resource id for 语音合成(小模型版); recorded in manifest,
# not sent as an X-Api-Resource-Id header on the v1 endpoint.
DOUBAO_TTS_RESOURCE_ID=volc.tts.default

# ASR: one-sentence small-model. Prefer the shared API key above. If the
# new-console API key is not accepted by the selected old v2 service, use the
# legacy small-model credentials instead (VOLCENGINE_ASR_AUTH=legacy).
VOLCENGINE_ASR_ENDPOINT=wss://openspeech.bytedance.com/api/v2/asr
# Optional legacy fallback (generic names are also accepted):
# VOLCENGINE_ASR_APP_ID=       # or VOLCENGINE_APP_ID
# VOLCENGINE_ASR_ACCESS_TOKEN= # or VOLCENGINE_ACCESS_TOKEN
# VOLCENGINE_ASR_CLUSTER=volcengine_input  # or VOLCENGINE_CLUSTER; not SECRET_KEY
# VOLCENGINE_ASR_AUTH=legacy
# VOLCENGINE_ASR_RESOURCE_ID=volc.onesentenceasr.office.cn

# Legacy speech synthesis 2.0 fallback only:
# DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts
# DOUBAO_RESOURCE_ID=seed-tts-2.0
```

## TTS: standard small-model by default

`tools/voice/tts_once.py` and the tutorial scripts (`animation/tts_doubao.py`,
`animation/compile_tutorial.py`, `animation/rebuild_tutorial.py`) use the
standard small-model service unless explicitly switched back:

```bash
# default: standard small-model WebSocket v1
python tools/voice/tts_once.py \
  --text "这是一次语音测试" \
  --out-file /tmp/test.mp3

# legacy speech synthesis 2.0
python tools/voice/tts_once.py \
  --provider seed2 \
  --text "这是一次语音测试" \
  --out-file /tmp/test-seed2.mp3
```

Confirmed standard-service parameters:

| Item | Value |
|---|---|
| Endpoint | `wss://openspeech.bytedance.com/api/v1/tts/ws_binary` |
| Auth (new console) | `X-Api-Key: ${VOLCENGINE_API_KEY}` |
| Auth (legacy) | `Authorization: Bearer; ${VOLCENGINE_TTS_ACCESS_TOKEN}` + `app.appid/token/cluster` |
| Cluster | `volcano_tts` for standard small-model v1 |
| Voice parameter | `audio.voice_type` |
| Recommended tutorials/QA voice | `BV700_streaming` (灿灿，中文女声，通用/讲故事). Safe alternatives: `BV001_streaming` (通用女声), `BV002_streaming` (通用男声) |
| Audio format | `audio.encoding=mp3`, `audio.rate=24000` |
| Request fields | `audio.voice_type`, `audio.encoding`, `audio.rate`, `audio.speed_ratio`, `audio.volume_ratio`, `audio.pitch_ratio`; `request.operation=submit`, `request.text`, `request.text_type=plain`, `request.reqid` |
| Response | binary audio-only server response; no v3 subtitle events |
| Subtitle timing | **unavailable through this implementation**. `*.subtitle.json` is not written for standard TTS. The runtime must degrade without pretending word-level timestamps exist. |

The seed-tts-2.0 v3 code path is still present and can be selected with
`--provider seed2` or `DOUBAO_TTS_PROVIDER=seed2`.

`tts_once.py` always outputs `{"file":"...","duration":3.21}` on stdout and
always returns mp3 bytes through `/api/tts`; Android `TtsRepository` does not
need to change because the content type remains `audio/mpeg`.

## ASR: one-sentence small-model

`tools/voice/asr_once.py` still uses the v2 one-sentence WebSocket protocol:

| Item | Value |
|---|---|
| Endpoint | `wss://openspeech.bytedance.com/api/v2/asr` |
| Shared API key mode | `X-Api-Key: ${VOLCENGINE_API_KEY}`; `appid` is not required by the new console |
| Legacy mode | `Authorization: Bearer; ${VOLCENGINE_ASR_ACCESS_TOKEN}` / `VOLCENGINE_ACCESS_TOKEN` plus `app.appid/token/cluster` |
| Legacy cluster | one-sentence small-model cluster is `volcengine_input` (not TTS `volcano_tts`); configure `VOLCENGINE_ASR_CLUSTER` or `VOLCENGINE_CLUSTER`. `VOLCENGINE_SECRET_KEY` is HMAC signing material, not a cluster. |
| Audio | 16 kHz, 16-bit, mono WAV |
| Return | `{"text":"...","request_id":"...","log_id":"..."}` on stdout |

If the shared API key is not accepted by the old v2 small-model endpoint for
your project, set `VOLCENGINE_ASR_AUTH=legacy` and fill the three legacy
credentials. The script reports the failure to stderr and exits non-zero; it
does not fake a successful transcription.

Real test result: standard TTS works with the shared `X-Api-Key`; the old v2
one-sentence small-model endpoint is authenticated with the APP's
AppID/AccessToken plus cluster `volcengine_input`. Example:

```dotenv
VOLCENGINE_APP_ID=...
VOLCENGINE_ACCESS_TOKEN=...
VOLCENGINE_ASR_CLUSTER=volcengine_input
```

`asr_once.py` automatically prefers these legacy credentials when all three
are present. Set `VOLCENGINE_ASR_AUTH=apikey` only to force the shared-key
experiment.

Android keeps calling:

```text
POST /api/asr/once
Content-Type: audio/wav
Body: WAV bytes
```

The backend still returns `{"text":"...","request_id":"..."}`.

## Failure behavior

Both scripts write human-readable errors to stderr and exit non-zero on
missing dependencies, missing credentials, timeout, protocol errors, empty
recognition or empty audio output. `BoardAI.Api` captures stdout/stderr
asynchronously, applies a timeout, and deletes its temporary WAV/text/mp3
files after each request.
