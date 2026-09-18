#!/usr/bin/env bash
# 两个工作区（WSL / Windows）的同步工具。
#
# 背景：这两个目录是**同一条历史**的两个工作区 ——
#   Windows  D:\workspace\board        ← Unity 实际读的那份（动画数据在这里改）
#   WSL      /home/cui/workspace/board ← 跑工具的那份（校验、对账、TTS）
# GitHub(origin) 是共同真相；本地还有两个 remote：WSL 里的 `windows`、Windows 里的 `wsl`，
# 断网时也能互相同步。
#
# 用法：
#   ./scripts/sync_workspaces.sh status         两边状态 + 是否一致 + 缺哪些提交
#   ./scripts/sync_workspaces.sh from-windows   把 Windows 的提交同步到 WSL（**改动画之后的常规动作**）
#   ./scripts/sync_workspaces.sh from-linux     把 WSL 的提交同步到 Windows（改工具/文档之后）
#   ./scripts/sync_workspaces.sh push           把 WSL 的 main 推到 GitHub
#
# 原则：**只快进（--ff-only）**。两边各有新提交（分叉）时它拒绝并让你决定怎么合 ——
# 绝不 force、绝不 reset、绝不碰本地未跟踪的素材（games/*/media、client/CaptureOut）。
# 工作区里有未提交的**已跟踪**改动时也拒绝：先 commit 或 stash，别让同步把改动卷进来。
set -u
LINUX=/home/cui/workspace/board
WIN=/mnt/d/workspace/board
BRANCH=main

die() { echo "✗ $*" >&2; exit 1; }

# 已跟踪文件是否干净（未跟踪的本地素材不算脏）
tracked_clean() { [ "$(git -C "$1" status --porcelain --untracked-files=no | wc -l)" -eq 0 ]; }
head_of()   { git -C "$1" log --oneline -1 2>/dev/null; }
branch_of() { git -C "$1" branch --show-current 2>/dev/null; }
count()     { git -C "$1" rev-list --count "$2" 2>/dev/null || echo "?"; }

show() {
  local name=$1 dir=$2
  if [ ! -d "$dir/.git" ]; then echo "  $name: 找不到仓库 $dir"; return; fi
  local h b
  h=$(head_of "$dir"); b=$(branch_of "$dir")
  printf "  %-8s %s  [%s]%s\n" "$name" "$h" "$b" \
    "$(tracked_clean "$dir" && echo "" || echo "  ⚠ 有未提交的已跟踪改动")"
}

status() {
  echo "工作区状态"
  show Windows "$WIN"
  show Linux   "$LINUX"
  if [ -d "$WIN/.git" ] && [ -d "$LINUX/.git" ]; then
    # 先取一次本地 remote 的引用，否则算不出领先/落后（对象都在本地，不联网）
    git -C "$LINUX" fetch -q windows 2>/dev/null || true
    git -C "$LINUX" fetch -q origin 2>/dev/null || true
    local wl lw
    wl=$(count "$LINUX" "$BRANCH..windows/$BRANCH")   # Windows 独有的提交
    lw=$(count "$LINUX" "windows/$BRANCH..$BRANCH")   # WSL 独有的提交
    echo
    echo "两个工作区的差异"
    echo "  Windows 比 WSL 多: $wl 个提交"
    echo "  WSL 比 Windows 多: $lw 个提交"
    if [ "$wl" = "0" ] && [ "$lw" = "0" ]; then
      echo "  → 两边一致 ✓"
    elif [ "$wl" != "0" ] && [ "$lw" != "0" ]; then
      echo "  → **分叉了**：两边各有新提交，需要人来决定怎么合（不要 force）"
    elif [ "$wl" != "0" ]; then
      echo "  → 跑 from-windows（Windows 领先，动画是在那边改的）"
    else
      echo "  → 跑 from-linux（WSL 领先）"
    fi
    echo
    echo "与 GitHub 的差异"
    echo "  未推送: $(count "$LINUX" "origin/$BRANCH..$BRANCH") 个；未拉取: $(count "$LINUX" "$BRANCH..origin/$BRANCH") 个"
  fi
}

# 同步方向 1：Windows → WSL
from_windows() {
  [ -d "$WIN/.git" ] || die "找不到 Windows 仓库 $WIN"
  tracked_clean "$LINUX" || die "WSL 工作区有未提交的已跟踪改动 —— 先 commit 或 stash"
  git -C "$LINUX" fetch windows || die "fetch windows 失败"
  if ! git -C "$LINUX" merge --ff-only "windows/$BRANCH" >/dev/null 2>&1; then
    die "不是快进（两边各有新提交）。先看清：./scripts/sync_workspaces.sh status"
  fi
  echo "✓ WSL 已快进到 $(head_of "$LINUX")"
}

# 同步方向 2：WSL → Windows
from_linux() {
  [ -d "$WIN/.git" ] || die "找不到 Windows 仓库 $WIN"
  tracked_clean "$WIN" || die "Windows 工作区有未提交的已跟踪改动 —— 先在那边 commit 或 stash"
  git -C "$WIN" fetch wsl || die "fetch wsl 失败"
  if ! git -C "$WIN" merge --ff-only "wsl/$BRANCH" >/dev/null 2>&1; then
    die "不是快进（两边各有新提交）。先看清：./scripts/sync_workspaces.sh status"
  fi
  echo "✓ Windows 已快进到 $(head_of "$WIN")"
}

push_origin() {
  tracked_clean "$LINUX" || die "WSL 工作区有未提交的已跟踪改动"
  echo "推送 WSL 的 $BRANCH → origin（只快进）"
  local out
  if ! out=$(git -C "$LINUX" push origin "$BRANCH" 2>&1); then
    # 分清是"网络不通"还是"远端有新提交"——两件事的处理完全不同
    if echo "$out" | grep -qiE 'could not resolve|unable to access|TLS|timed out|Connection'; then
      die "推送失败：**网络问题**（GitHub 连不上）。本地两个工作区的同步不受影响，用 from-windows / from-linux 即可。"
    fi
    die "推送被拒：远端有我们没有的提交 —— 先 fetch 看清楚，别 force"
  fi
  echo "$out" | tail -1
}

case "${1:-status}" in
  status)        status ;;
  from-windows)  from_windows ;;
  from-linux)    from_linux ;;
  push)          push_origin ;;
  -h|--help)     sed -n '2,25p' "$0" ;;
  *)             die "未知参数 '$1'（status / from-windows / from-linux / push）" ;;
esac
