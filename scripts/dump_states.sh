#!/usr/bin/env bash
# 采样若干条 cue 的终态，写入 script/full/<cue>.exitstate.json
# 供 check_cue_script.py --chain 做跨 cue 对账。
set -u
UNITY="/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe"
PROJ='D:\workspace\board\client'
ROOT=/home/cui/workspace/board
WIN_SCRIPT='D:\workspace\board\games\splendor\tutorial\script\full'
CUES="${*:-setup.cards.001.1 setup.cards.001.2 setup.cards.001.3 setup.cards.002.1}"
ok=0; bad=0

for cue in $CUES; do
  "$UNITY" -batchmode -projectPath "$PROJ" \
    -executeMethod BoardGameTutorial.Editor.TutorialFrameCapture.DumpState \
    -dumpCue "$cue" -dumpReplay 1 -dumpOut "$WIN_SCRIPT\\${cue}.exitstate.json" \
    -logFile "D:\\workspace\\board\\client\\Logs\\dump_${cue}.log" -quit >/dev/null 2>&1
  # 同步回 WSL 仓库（引擎写的是 Windows 侧）
  cp "/mnt/d/workspace/board/games/splendor/tutorial/script/full/${cue}.exitstate.json" \
     "$ROOT/games/splendor/tutorial/script/full/${cue}.exitstate.json" 2>/dev/null
  if [ -f "$ROOT/games/splendor/tutorial/script/full/${cue}.exitstate.json" ]; then
    echo "  OK   $cue"; ok=$((ok+1))
  else
    echo "  FAIL $cue（见 Logs/dump_${cue}.log）"; bad=$((bad+1))
  fi
done
echo "采样完成：$ok 成功 / $bad 失败"
