# Voice bridge (ASR + TTS)

This directory contains the v1 Python bridge used by `BoardAI.Api`.  The API
starts these scripts as short-lived child processes; Android never talks to
Volcengine directly and no key is shipped in the APK.

## Dependencies

```bash
pip install -r animation/requirements-tts.txt
```

`websockets` is used by both scripts. `mutagen` is used by the existing
`animation/tts_doubao.py` to report mp3 duration.

## Credentials

Put the following in the repository-root `.env` (already ignored by git) or
export them in the shell before starting the API:

```dotenv
# ASR (Volcengine one-sentence WebSocket API v2)
VOLCENGINE_ASR_APP_ID=
VOLCENGINE_ASR_ACCESS_TOKEN=
VOLCENGINE_ASR_CLUSTER=
# Optional:
# VOLCENGINE_ASR_ENDPOINT=wss://openspeech.bytedance.com/api/v2/asr
# VOLCENGINE_ASR_UID=boardai-android
# VOLCENGINE_ASR_WORKFLOW=audio_in,resample,partition,vad,fe,decode,itn,nlu_punctuate

# TTS (Doubao speech synthesis 2.0, same credentials as the tutorial pipeline)
VOLCENGINE_API_KEY=
# Optional:
# DOUBAO_SPEAKER=zh_female_vv_uranus_bigtts
# DOUBAO_RESOURCE_ID=seed-tts-2.0
```

`asr_once.py` uses token auth: it sends
`Authorization: Bearer; <VOLCENGINE_ASR_ACCESS_TOKEN>` and also puts
`appid/token/cluster` in the first full-client-request JSON payload.  The
cluster value must be the Cluster ID shown in the Volcengine console after the
one-sentence ASR service is enabled.  If any of the three values is missing the
script exits non-zero and the API returns an error instead of hard-coding a
credential.

`tts_once.py` reuses `animation/tts_doubao.py` for the WebSocket v3
connection, `X-Api-Key` auth, request payload, synthesis loop and mp3 duration.
It only wraps that existing code in a one-text CLI; it does not modify the
tutorial compilation chain.

## ASR usage

```bash
python tools/voice/asr_once.py --input /absolute/path/test.wav
# {"text":"这个游戏怎么拿宝石？","request_id":"...","log_id":"..."}
```

The API only accepts `Content-Type: audio/wav`, bodies up to 10 MB and WAV
duration up to 60 seconds.  The script itself relies on the v2 one-sentence
WebSocket protocol and segments the WAV bytes into roughly 10-second packets.

## TTS usage

```bash
python tools/voice/tts_once.py \
  --text "这是一次语音测试" \
  --out-file /tmp/test.mp3 \
  --voice zh_female_vv_uranus_bigtts \
  --speed 1.0
# {"file":"/tmp/test.mp3","duration":3.21}
```

`--text-file` is the preferred form for arbitrary text from the API.  The
friendly `--speed` value is mapped to the Doubao `speech_rate` field:
`1.0 -> 0`, `2.0 -> 100`, `0.5 -> -50`.

## Failure behavior

Both scripts write human-readable errors to stderr and exit non-zero on
missing dependencies, missing credentials, timeout, protocol errors, empty
recognition or empty audio output.  `BoardAI.Api` captures stdout/stderr
asynchronously, applies a timeout, and deletes its temporary WAV/text/mp3
files after each request.
