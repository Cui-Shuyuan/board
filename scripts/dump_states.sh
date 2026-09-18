#!/usr/bin/env bash
# 采样 cue 的**终态** → games/splendor/tutorial/anim/full.exitstate.json
# 供 scripts/check_cue_script.py 与动画脚本（anim/full.json）里的契约做 diff。
#
# 一次 Unity 启动、从轨道头顺次播到尾，每条 cue 播到终态就记一笔 ——
# 这正是播放器的真实路径（顺序播放），比一条条 cue 各自重放更接近用户看到的画面；
# 顺带把「109 条 cue = 109 次 Unity 启动」降成 1 次。
#
#   ./scripts/dump_states.sh                 # 整条 full 轨道（默认）
#   ./scripts/dump_states.sh --cues a,b,c    # 只采这几条（按给定顺序；前面的会被重放）
#
# 前提：Windows 侧工作区要先和本仓库同步（git push / pull），否则 Unity 读到的是旧动画数据
# —— 两个工作区的关系见 .claude/memory/workspace-sync.md。
set -u
UNITY="/mnt/d/Unity/Hub/Editor/6000.5.8f1/Editor/Unity.exe"
PROJ='D:\workspace\board\client'
ROOT=/home/cui/workspace/board
# 采样落到哪：**同一个相对路径**的两侧写法（写歪过一次：让 Unity 写到 script\、
# 却从 anim\ 拷回来，于是每次"重跑采样"其实拷的都是仓库里那份旧文件 ——
# 表现就是"采样是旧格式，重跑一下就好"，而重跑并没有用）。
WIN_REL='games\splendor\tutorial\anim\full.exitstate.json'
WIN_OUT="D:\\workspace\\board\\$WIN_REL"
WSL_OUT="$ROOT/games/splendor/tutorial/anim/full.exitstate.json"
WIN_SRC="/mnt/d/workspace/board/games/splendor/tutorial/anim/full.exitstate.json"
LOG='D:\workspace\board\client\Logs\dump_states.log'

CUES=""
while [ $# -gt 0 ]; do
  case "$1" in
    --cues)   CUES="${2:-}"; shift 2 ;;
    --cues=*) CUES="${1#--cues=}"; shift ;;
    -h|--help) sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "未知参数: $1（用 --cues a,b,c）" >&2; exit 2 ;;
  esac
done

if [ -z "$CUES" ]; then
  CUES=$(python3 -c "
import json
d = json.load(open('$ROOT/games/splendor/tutorial/full.runtime.json'))
print(','.join(c['id'] for c in d['cues']))")
fi

echo "一次 Unity 启动，采样 $(( $(echo "$CUES" | tr -cd ',' | wc -c) + 1 )) 条 cue"
rm -f "$WSL_OUT"
"$UNITY" -batchmode -projectPath "$PROJ" \
  -executeMethod BoardGameTutorial.Editor.TutorialFrameCapture.DumpState \
  -dumpCues "$CUES" -dumpOut "$WIN_OUT" \
  -logFile "$LOG" -quit >/dev/null 2>&1

# 引擎写的是 Windows 侧的工作区，拷回 WSL 仓库
cp "$WIN_SRC" "$WSL_OUT" 2>/dev/null

if [ -f "$WSL_OUT" ]; then
  python3 -c "
import json
d = json.load(open('$WSL_OUT'))
n = len(d['cues'])
k = next(iter(d['cues'].values()))
print(f'  OK   {n} 条 cue 的终态 → $WSL_OUT')
print(f'       格式：picture={\"有\" if \"picture\" in k else \"**缺**\"}，'
      f'items={\"有（\" + str(len(k.get(\"items\") or [])) + \" 件）\" if \"items\" in k else \"**缺**\"}'
      f'（缺 = 采样没重跑到，对账会拿旧格式比）')"
else
  echo "  FAIL 采样失败（见 client/Logs/dump_states.log）" >&2
  exit 1
fi
