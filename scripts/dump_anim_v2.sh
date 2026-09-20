#!/usr/bin/env bash
# V2 sampling: Unity batchmode -> games/{game}/tutorial/anim/v2/{track}.v2sample.json
set -u
UNITY="/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe"
PROJ='D:\workspace\board\client'
ROOT=/home/cui/workspace/board
GAME=splendor
TRACK=full
CUES=""
while [ $# -gt 0 ]; do
  case "$1" in
    --game) GAME="$2"; shift 2 ;;
    --track) TRACK="$2"; shift 2 ;;
    --cues) CUES="$2"; shift 2 ;;
    --cues=*) CUES="${1#--cues=}"; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done
if [ -z "$CUES" ]; then
  CUES=$(python3 -c "
import json
p='$ROOT/games/$GAME/tutorial/anim/v2/$TRACK.anim.json'
d=json.load(open(p))
print(','.join(c['id'] for c in d['cues']))")
fi
WIN_COMPILED="D:\\workspace\\board\\games\\$GAME\\tutorial\\anim\\v2\\$TRACK.compiled.json"
WIN_OUT="D:\\workspace\\board\\games\\$GAME\\tutorial\\anim\\v2\\$TRACK.v2sample.json"
WSL_OUT="$ROOT/games/$GAME/tutorial/anim/v2/$TRACK.v2sample.json"
WIN_SRC="/mnt/d/workspace/board/games/$GAME/tutorial/anim/v2/$TRACK.v2sample.json"
LOG="D:\\workspace\\board\\client\\Logs\\dump_anim_v2.log"
rm -f "$WSL_OUT"
"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod BoardGameTutorial.Editor.TutorialV2Sampler.DumpStateV2 \
  -v2Compiled "$WIN_COMPILED" -v2Out "$WIN_OUT" -v2Cues "$CUES" \
  -logFile "$LOG" -quit >/dev/null 2>&1
cp "$WIN_SRC" "$WSL_OUT" 2>/dev/null
if [ -f "$WSL_OUT" ]; then
  python3 -c "import json; d=json.load(open('$WSL_OUT')); print('OK  sampled',len(d['cues']),'cues -> $WSL_OUT')"
else
  echo "FAIL: no sample produced (see $LOG)" >&2; exit 1
fi
