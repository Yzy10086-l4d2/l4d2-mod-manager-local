#!/usr/bin/env bash
# 识别回归脚本：编译 -> 用生产函数跑库 -> 与基线快照逐行对比
# 用法：tools/ident_regress.sh "<库目录>" "<基线tsv>"
# 说明：只读；临时产物写在 D:/tmp/ident-regress（C 盘 Temp 在本机 csc 写不进），可用 IDENT_REGRESS_TMP 覆盖。
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="${1:-D:\游戏内容\求生之路2各项MOD}"
BASE="${2:-${PARITY_BASE:-}}"
WORK="${IDENT_REGRESS_TMP:-/d/tmp/ident-regress}"
mkdir -p "$WORK" || { echo "[FAIL] 临时目录不可写：$WORK"; exit 2; }
OUT="$(cygpath -w "$WORK")\\parity.tsv"
EXE="$(cygpath -w "$WORK")\\ident-regress.exe"
LOG="$WORK/build.log"

if [ -z "$BASE" ]; then echo "[FAIL] 缺少基线路径（参数2 或 PARITY_BASE）" >&2; exit 2; fi
[ -f "$BASE" ] || { echo "[FAIL] 基线不存在：$BASE" >&2; exit 2; }

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
[ -x "$CSC" ] || CSC="/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"
[ -x "$CSC" ] || { echo "[FAIL] 找不到 csc.exe" >&2; exit 2; }

echo "[1/3] 编译 -> $EXE"
( cd "$REPO" && "$CSC" /nologo /codepage:65001 /target:winexe /optimize+ /out:"$EXE" \
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll \
  /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll \
  /reference:System.IO.Compression.FileSystem.dll \
  *.cs > "$LOG" 2>&1 ) || { echo "[FAIL] 编译失败："; tail -20 "$LOG"; exit 1; }

echo "[2/3] 生成快照…"
"$EXE" --parityid="$ROOT" --parityout="$OUT" || { echo "[FAIL] 快照生成失败"; exit 1; }

echo "[3/3] 对比基线…"
DIFF="$(diff -u "$BASE" "$OUT" || true)"
if [ -z "$DIFF" ]; then
  echo "[PASS] 与基线完全一致（$(($(wc -l < "$OUT") - 1)) 个包）"
  exit 0
fi
echo "[FAIL] 有差异（前 40 行）："
echo "$DIFF" | head -40
echo "[i] 完整快照：$OUT"
exit 1