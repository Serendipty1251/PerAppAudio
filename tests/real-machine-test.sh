#!/usr/bin/env bash
# =============================================================================
# PerAppAudio 实机（真实音频）验收 —— ctl watch 常驻下的配对差分
#
# v2 教训：ctl add 是一次性命令，A 链路随该进程退出立即拆除（CLI 输出有提示：
#          「常驻迁移请运行 ctl watch」）。一次性 add 之后再测能量，A 早已拆掉，所以平。
# v3 对策：先起 ctl watch 常驻，由它维护 A 链路；配对测「前 / 中 / 后」。
#
# 设备 ID 已参数化（自动取默认控制台设备 / 第一个非默认活动设备）；
# 可用环境变量覆盖：DEF_ID=... TGT_ID=...
# 注意：本机背景有 wallpaper 引擎等常驻音频，判定需容差（脚本内 gt 带 0.015 容差）。
# =============================================================================
set -u
export PATH=/usr/bin:/bin:/c/Windows/System32:/c/Windows:$PATH

ROOT="/c/Users/78396/WorkBuddy/2026-10-15-16-34-02"
cd "$ROOT" || exit 1
CLI="./src/App.Cli/bin/Release/net8.0-windows/PerAppAudio.App.Cli.exe"
PY="C:/Users/78396/.workbuddy/binaries/python/versions/3.13.12/python.exe"
# 默认设备：带「控制台」角色的 Active 设备；目标设备：第一个非默认 Active 设备（可覆盖）
DEF="${DEF_ID:-$("$CLI" devices 2>/dev/null | awk '/^ *[0-9]+ /{a=($0 ~ /Active/); c=($0 ~ /控制台/)} /^ *ID: /{if(a&&c){print $2; exit}}')}"
TGT="${TGT_ID:-$("$CLI" devices 2>/dev/null | awk -v def="$DEF" '/^ *[0-9]+ /{a=($0 ~ /Active/)} /^ *ID: /{if(a && $2!=def){print $2; exit}}')}"

MS=1200
DUR=200000

rms(){ "$CLI" devlisten --device "$1" --ms "$MS" 2>/dev/null | awk '/\[结果\]/{print $3}'; }
gt(){ awk -v a="$1" -v b="$2" 'BEGIN{exit !(a>b+0.015)}'; }   # 容差 0.015：背景音波动大
say(){ printf '\n========== %s ==========\n' "$*"; }
PASSN=0; FAILN=0
verdict(){ if [ "$1" = "1" ]; then PASSN=$((PASSN+1)); echo "判定：✅ $2"; else FAILN=$((FAILN+1)); echo "判定：⚠️ $2"; fi; }

# ---------- 起音源 + 常驻协调器 ----------
say "0/6 起测试音源（playwav 440Hz ${DUR}ms）+ ctl watch 常驻"
"$CLI" playwav --ms "$DUR" > /tmp/perapp-pw.log 2>&1 &
PWBG=$!
"$CLI" ctl watch --interval 1000 > /tmp/perapp-watch.log 2>&1 &
WP=$!
sleep 2
PW=$(sed -n 's/.*本进程 PID \([0-9][0-9]*\).*/\1/p' /tmp/perapp-pw.log | head -1)
echo "playwav PID = $PW ；watch PID = $WP"
[ -z "$PW" ] && { echo "拿不到 PID，中止"; kill $PWBG $WP 2>/dev/null; exit 1; }
echo "--- 会话列表 ---"; "$CLI" sessions 2>/dev/null | grep -w "$PW" || echo "⚠️ 不可见"
BASE=$(rms "$DEF"); echo "音源在默认设备 RMS = $BASE"

scenario(){ # $1=标题 $2=mode $3=期望(migrate|keep)
  local title="$1" mode="$2" expect="$3" before during after tgt
  say "$title"
  before=$(rms "$DEF")
  local args=(--pid "$PW" --to "$TGT")
  [ "$mode" != "auto" ] && args+=(--mode "$mode")
  "$CLI" ctl add "${args[@]}" 2>&1 | grep -E "B2 持久化|A 实时|总评" || true
  sleep 4                                    # 等 watch 接管并维持 A 链路
  during=$(rms "$DEF"); tgt=$(rms "$TGT")
  echo "默认设备：$before → $during    目标设备：$tgt"
  "$CLI" ctl remove --pid "$PW" >/dev/null 2>&1
  sleep 3
  after=$(rms "$DEF")
  echo "拆除后：$after"
  if [ "$expect" = "migrate" ]; then
    if gt "$before" "$during" && gt "$after" "$during"; then verdict 1 "运行中的流被实时迁走，拆除后恢复（$before → $during → $after）"
    else verdict 0 "未见迁走（$before → $during → $after）"; fi
  else
    if gt "$during" "$before" || gt "$during" "$after"; then verdict 1 "仅 B2 不迁移运行中的流（符合平台结论）"
    else verdict 0 "判定失败（$before → $during → $after）"; fi
  fi
}

scenario "1/6 手段 Auto（B2 + A，watch 常驻）" auto migrate
scenario "2/6 仅手段 A（实时改道）"            a    migrate
scenario "3/6 仅手段 B2（平台结论：不迁移）"    b    keep

# ---------- 4. B2 set / get / clear ----------
say "4/6 B2 set → get → clear"
"$CLI" endpoint set --pid "$PW" --to "$TGT" 2>&1 | tail -1
R1=$("$CLI" endpoint get --pid "$PW" 2>&1 | tail -1)
"$CLI" endpoint clear --pid "$PW" 2>&1 | tail -1
R2=$("$CLI" endpoint get --pid "$PW" 2>&1 | tail -1)
echo "set 后读回   ：$R1"
echo "clear 后读回 ：$R2"
case "$R1" in *860ce70d*) verdict 1 "B2 set 后读回目标设备 ID" ;; *) verdict 0 "B2 读回不符：$R1" ;; esac
case "$R2" in *"跟随系统默认"*) verdict 1 "B2 clear 后恢复跟随系统默认" ;; *) verdict 0 "clear 后读回不符：$R2" ;; esac

# ---------- 5. 看门狗 ----------
say "5/6 看门狗：音源进程退出 → 自动拆除"
"$CLI" ctl add --pid "$PW" --to "$TGT" >/dev/null 2>&1
sleep 3
echo "结束 playwav（PID $PW）…"
kill "$PW" 2>/dev/null; sleep 1; kill -9 "$PW" 2>/dev/null
sleep 5
echo "--- watch 日志（拆除相关）---"
grep -E "已退出|拆除|暂停|重挂" /tmp/perapp-watch.log | tail -5 || echo "（未见）"
if grep -q "已退出" /tmp/perapp-watch.log; then verdict 1 "看门狗检测到进程退出并拆除"; else verdict 0 "未见看门狗拆除日志"; fi
kill "$WP" 2>/dev/null; kill $PWBG 2>/dev/null

# ---------- 6. 清理与汇总 ----------
say "6/6 清理与汇总"
"$PY" - <<'PYEOF'
import json, os
p = os.path.join(os.environ["APPDATA"], "PerAppAudio", "routes.json")
if not os.path.isfile(p):
    print("routes.json 不存在"); raise SystemExit
rows = json.load(open(p, encoding="utf-8"))
keep = [r for r in rows if "perappaudio.app.cli.exe" not in r.get("ProcessPath", "").lower()]
json.dump(keep, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(f"清理测试路由：{len(rows)} → {len(keep)} 条")
PYEOF
echo "--- routes.json 终态 ---"; "$CLI" ctl list 2>&1 | sed -n '1,4p'
echo "--- 残留进程 ---"; tasklist //FI "IMAGENAME eq PerAppAudio.App.Cli.exe" 2>/dev/null | grep -ci cli | sed 's/^/CLI 进程数: /'
echo
echo "基线 RMS = $BASE"
echo "通过 $PASSN 项 / 未过 $FAILN 项"
