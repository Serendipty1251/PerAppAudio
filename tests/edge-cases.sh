#!/usr/bin/env bash
# =============================================================================
# PerAppAudio CLI 极限 / 边界测试 v2（tests/edge-cases.sh）
#
# 目的：在各种异常与极限输入下验证 CLI 不崩溃（无 .NET 运行时级未处理异常）。
#
# 判定标准（v2 修正）：
#   ok    —— 退出码符合预期，且无「运行时级」崩溃特征
#   CRASH —— 出现 Unhandled Exception / 0xC0000005 / Fatal Execution Engine Error /
#            clr20r3 / core dumped（这些才是真正的进程崩溃）
#   EXIT  —— 退出码与预期不符（属优雅失败，仅记录）
#   注意：CLI 的 Main 会捕获异常并以「错误: ...」+ 堆栈打印（ex.ToString()），
#         这是**已处理的**优雅失败，不算崩溃——v1 曾把它误报为 CRASH。
#
# 用法：bash tests/edge-cases.sh [--quick]
# 配置破坏类用例经 Python 改写 %APPDATA%\PerAppAudio\{routes.json,rules.yaml}，
# 退出时自动还原。
# =============================================================================
set -u
export PATH=/usr/bin:/bin:/c/Windows/System32:/c/Windows:$PATH

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 1

CLI="src/App.Cli/bin/Release/net8.0-windows/PerAppAudio.App.Cli.exe"
CFG="/c/Users/78396/AppData/Roaming/PerAppAudio"
LOG="output/edge-test.log"
QUICK=0
[ "${1:-}" = "--quick" ] && QUICK=1
PY="C:/Users/78396/.workbuddy/binaries/python/versions/3.13.12/python.exe"

PASS=0; SOFT=0; CRASH=0
CRASH_DETAIL=""

# ---- 取一个在线设备 / 麦克风的完整 ID（devices 列表会把友好名截断成 …，不能回填）----
DEV_ID="$(./"$CLI" devices 2>/dev/null | awk '/^ *[0-9]+ /{a=($0 ~ /Active/)} /^ *ID: /{if(a){print $2; exit}}')"
MIC_ID="$(./"$CLI" mic list 2>/dev/null | awk '/^ *[0-9]+ /{a=($0 ~ /Active/)} /^ *ID: /{if(a){print $2; exit}}')"
[ -z "$DEV_ID" ] && DEV_ID="Speaker (Realtek(R) Audio)"
[ -z "$MIC_ID" ] && MIC_ID="Microphone Array (Realtek(R) Audio)"
SELF_PID=$$

log(){ printf '%s\n' "$*" | tee -a "$LOG"; }

check(){ # $1=描述  $2=期望退出码(0|1|2|any)  其余=命令
  local desc="$1" expect="$2"; shift 2
  local out ec status
  out="$("$@" 2>&1)"; ec=$?
  if printf '%s' "$out" | grep -qiE 'unhandled exception|accessviolation|0xc0000005|fatal execution engine|clr20r3|core dumped|SIGSEGV|SIGABRT|external component.*thrown'; then
    status="CRASH"
  elif [ "$expect" != "any" ] && [ "$ec" != "$expect" ]; then
    status="EXIT($ec)"
  else
    status="ok"
  fi
  printf '%-54s %-8s ec=%-3s %s\n' "$desc" "$status" "$ec" "$(printf '%s' "$out" | head -1 | cut -c1-44)" | tee -a "$LOG"
  case "$status" in
    ok)    PASS=$((PASS+1)) ;;
    EXIT*) SOFT=$((SOFT+1)) ;;
    CRASH) CRASH=$((CRASH+1)); CRASH_DETAIL="$CRASH_DETAIL
===== $desc (ec=$ec) =====
$(printf '%s' "$out" | head -20)" ;;
  esac
}

section(){ printf '\n--- %s ---\n' "$1" | tee -a "$LOG"; }

# ---- 配置备份 / 还原（全走 Python，避开 rm 的路径限制）----
BK="$(mktemp -d)"
"$PY" - "$CFG" "$BK" <<'PYEOF'
import sys, os, shutil
cfg, bk = sys.argv[1], sys.argv[2]
for f in ("routes.json", "rules.yaml"):
    src = os.path.join(cfg, f)
    if os.path.isfile(src):
        shutil.copy2(src, os.path.join(bk, f))
PYEOF
restore_files(){ "$PY" - "$CFG" "$BK" <<'PYEOF'
import sys, os, shutil
cfg, bk = sys.argv[1], sys.argv[2]
for f in ("routes.json", "rules.yaml"):
    dst = os.path.join(cfg, f)
    src = os.path.join(bk, f)
    if os.path.isfile(src):
        shutil.copy2(src, dst)
    elif os.path.isdir(dst):          # 目录用例的残留：os.remove 对目录会 PermissionError
        shutil.rmtree(dst, ignore_errors=True)
    elif os.path.exists(dst):
        os.remove(dst)
PYEOF
}
cleanup_bk(){ "$PY" - "$BK" <<'PYEOF'
import sys, shutil
shutil.rmtree(sys.argv[1], ignore_errors=True)
PYEOF
}
trap 'restore_files; cleanup_bk' EXIT

: > "$LOG"
log "CLI   : $CLI"
log "设备ID: $DEV_ID"
log "麦克ID: $MIC_ID"
log "无会话PID: $SELF_PID"

# ============================= 1. 参数与命令路由 =============================
section "参数与命令路由"
check "无参数"                     any ./"$CLI"
check "help"                       0   ./"$CLI" help
check "--help"                     0   ./"$CLI" --help
check "-h"                         0   ./"$CLI" -h
check "未知命令"                   2   ./"$CLI" not-a-command
check "空字符串命令"               2   ./"$CLI" ""
check "超长命令名(5000)"           2   ./"$CLI" "$(printf 'a%.0s' $(seq 1 5000))"

# ============================= 2. devices =============================
section "devices"
check "devices"                    0   ./"$CLI" devices
check "--device 缺值"              any ./"$CLI" devices --device
check "--device 不存在"            any ./"$CLI" devices --device "__NOPE__"
check "--device 空串"              any ./"$CLI" devices --device ""
check "--device 超长串(3000)"      any ./"$CLI" devices --device "$(printf 'b%.0s' $(seq 1 3000))"
check "--device emoji"             any ./"$CLI" devices --device "🎧🎧🎧"
check "--device 合法ID"            any ./"$CLI" devices --device "$DEV_ID"

# ============================= 3. sessions =============================
section "sessions"
check "sessions"                   0   ./"$CLI" sessions
check "sessions --device 不存在"   any ./"$CLI" sessions --device "__NOPE__"
check "sessions --device 合法ID"   any ./"$CLI" sessions --device "$DEV_ID"
check "sessions --device 空串"     any ./"$CLI" sessions --device ""

# ============================= 4. sessionvol =============================
section "sessionvol"
check "缺 --pid"                   2   ./"$CLI" sessionvol
check "--pid 0"                    any ./"$CLI" sessionvol --pid 0
check "--pid -1"                   any ./"$CLI" sessionvol --pid -1
check "--pid abc"                  any ./"$CLI" sessionvol --pid abc
check "--pid 999999999"            any ./"$CLI" sessionvol --pid 999999999
check "--pid 4(系统进程)"          any ./"$CLI" sessionvol --pid 4
check "--pid 自身shell(无会话)"    any ./"$CLI" sessionvol --pid "$SELF_PID"

# ============================= 5. endpoint（B2） =============================
section "endpoint"
check "probe"                      0   ./"$CLI" endpoint probe
check "无子命令"                   any ./"$CLI" endpoint
check "未知子命令"                 2   ./"$CLI" endpoint bogus
check "set 缺 --pid"               2   ./"$CLI" endpoint set
check "set 缺 --to"                any ./"$CLI" endpoint set --pid "$SELF_PID"
check "set 设备不存在"             any ./"$CLI" endpoint set --pid "$SELF_PID" --to "__NOPE__"
check "set 空设备名"               any ./"$CLI" endpoint set --pid "$SELF_PID" --to ""
check "set --pid 0"                any ./"$CLI" endpoint set --pid 0 --to "$DEV_ID"
check "set --pid -1"               any ./"$CLI" endpoint set --pid -1 --to "$DEV_ID"
check "set --pid abc"              any ./"$CLI" endpoint set --pid abc --to "$DEV_ID"
check "set 无会话进程(平台语义)"   any ./"$CLI" endpoint set --pid "$SELF_PID" --to "$DEV_ID"
check "set 系统进程(PID 4)"        any ./"$CLI" endpoint set --pid 4 --to "$DEV_ID"
check "get --pid 0"                any ./"$CLI" endpoint get --pid 0
check "get --pid 不存在"           any ./"$CLI" endpoint get --pid 999999999
check "get 无会话进程"             any ./"$CLI" endpoint get --pid "$SELF_PID"
check "clear --pid 0"              any ./"$CLI" endpoint clear --pid 0
check "clear --pid 不存在"         any ./"$CLI" endpoint clear --pid 999999999
check "clear 无会话进程"           any ./"$CLI" endpoint clear --pid "$SELF_PID"

# ============================= 6. mic =============================
section "mic"
check "list"                       0   ./"$CLI" mic list
check "未知子命令"                 2   ./"$CLI" mic bogus
check "set 缺 --pid"               2   ./"$CLI" mic set
check "set 缺 --to"                any ./"$CLI" mic set --pid "$SELF_PID"
check "set 设备不存在"             any ./"$CLI" mic set --pid "$SELF_PID" --to "__NOPE__"
check "set 空设备名"               any ./"$CLI" mic set --pid "$SELF_PID" --to ""
check "set --pid 0"                any ./"$CLI" mic set --pid 0 --to "$MIC_ID"
check "set 无会话进程(平台限制)"   any ./"$CLI" mic set --pid "$SELF_PID" --to "$MIC_ID"
check "get --pid 0"                any ./"$CLI" mic get --pid 0
check "get --pid 不存在"           any ./"$CLI" mic get --pid 999999999

# ============================= 7. proc =============================
section "proc"
check "list"                       0   ./"$CLI" proc list
check "list 过滤无匹配"            0   ./"$CLI" proc list "__NOPE__"
check "list 过滤空串"              0   ./"$CLI" proc list ""
check "list 过滤超长串(3000)"      0   ./"$CLI" proc list "$(printf 'c%.0s' $(seq 1 3000))"
check "list 过滤 explorer"         0   ./"$CLI" proc list explorer
check "list 过滤 emoji"            0   ./"$CLI" proc list "🎧"

# ============================= 8. ctl =============================
section "ctl"
check "list"                       0   ./"$CLI" ctl list
check "未知子命令"                 2   ./"$CLI" ctl bogus
check "add 缺 --pid"               2   ./"$CLI" ctl add
check "add 缺 --to"                any ./"$CLI" ctl add --pid "$SELF_PID"
check "add 设备不存在"             any ./"$CLI" ctl add --pid "$SELF_PID" --to "__NOPE__"
check "add 空设备名"               any ./"$CLI" ctl add --pid "$SELF_PID" --to ""
check "add --pid 0"                any ./"$CLI" ctl add --pid 0 --to "$DEV_ID"
check "add --pid -1"               any ./"$CLI" ctl add --pid -1 --to "$DEV_ID"
check "add --pid abc"              any ./"$CLI" ctl add --pid abc --to "$DEV_ID"
check "add --mode bogus"           any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" --mode bogus
check "add --mode a/b/endpoint"    any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" --mode a
check "add --volume 5(越界)"       any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" --volume 5
check "add --volume -3(越界)"      any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" --volume -3
check "add --volume abc"           any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" --volume abc
check "add 无会话进程(合法设备)"   any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID"
check "add 系统进程(PID 4)"        any ./"$CLI" ctl add --pid 4 --to "$DEV_ID"
check "add --pid 999999999"        any ./"$CLI" ctl add --pid 999999999 --to "$DEV_ID"
check "remove 缺 --pid"            2   ./"$CLI" ctl remove
check "remove --pid 0"             any ./"$CLI" ctl remove --pid 0
check "remove --pid 不存在"        0   ./"$CLI" ctl remove --pid 999999999
check "remove 自身shell"           0   ./"$CLI" ctl remove --pid "$SELF_PID"
check "add/remove 快速反复 10 轮"  any bash -c 'for i in $(seq 1 10); do '"$(printf '%q' "./$CLI")"' ctl add --pid '"$SELF_PID"' --to "'"$DEV_ID"'" >/dev/null 2>&1; '"$(printf '%q' "./$CLI")"' ctl remove --pid '"$SELF_PID"' >/dev/null 2>&1; done; echo ok'

# ============================= 9. profile =============================
section "profile"
check "list"                       0   ./"$CLI" profile list
check "current"                    0   ./"$CLI" profile current
check "apply 缺 --name"            any ./"$CLI" profile apply
check "apply 不存在"               any ./"$CLI" profile apply --name "__NOPE__"
check "apply --replace 不存在"     any ./"$CLI" profile apply --name "__NOPE__" --replace
check "未知子命令"                 2   ./"$CLI" profile bogus

# ============================= 10. route（A，短时长） =============================
section "route"
check "缺参数"                     any ./"$CLI" route
check "设备不存在"                 any ./"$CLI" route --pid "$SELF_PID" --to "__NOPE__" --ms 300
check "--pid 0"                    any ./"$CLI" route --pid 0 --to "$DEV_ID" --ms 300
check "--pid 999999999"            any ./"$CLI" route --pid 999999999 --to "$DEV_ID" --ms 300
check "无会话进程 300ms"           any ./"$CLI" route --pid "$SELF_PID" --to "$DEV_ID" --ms 300
check "空设备名"                   any ./"$CLI" route --pid "$SELF_PID" --to "" --ms 300
check "--ms abc"                   any ./"$CLI" route --pid "$SELF_PID" --to "$DEV_ID" --ms abc
check "--ms 0"                     any ./"$CLI" route --pid "$SELF_PID" --to "$DEV_ID" --ms 0
check "--buffer abc"               any ./"$CLI" route --pid "$SELF_PID" --to "$DEV_ID" --ms 300 --buffer abc
check "--buffer 0"                 any ./"$CLI" route --pid "$SELF_PID" --to "$DEV_ID" --ms 300 --buffer 0

# ============================= 11. devlisten =============================
section "devlisten"
check "缺 --device"                any ./"$CLI" devlisten
check "设备不存在"                 any ./"$CLI" devlisten --device "__NOPE__" --ms 300
check "在线设备 300ms"             any ./"$CLI" devlisten --device "$DEV_ID" --ms 300
check "--ms 0"                     any ./"$CLI" devlisten --device "$DEV_ID" --ms 0

# ============================= 12. test =============================
section "test"
check "COM 自检"                   any ./"$CLI" test

# ============================= 13. 配置文件极限 =============================
if [ "$QUICK" = "0" ]; then
section "配置文件极限"
corrupt(){ # $1=描述  $2=期望  $3=文件名  $4=写入方式(py 表达式)  其余=命令
  local desc="$1" expect="$2" fn="$3" mode="$4"; shift 4
  # 先把两个配置文件都还原，避免上一个用例的残留污染本用例（v2 修：rules.yaml 曾被
  # 上一个用例留下的垃圾 routes.json 掩盖，全部误报 JsonException）
  restore_files
  "$PY" - "$CFG/$fn" "$mode" <<'PYEOF'
import sys, os, shutil
path, mode = sys.argv[1], sys.argv[2]
data = {
  "garbage":  "{{{{not json",
  "empty":    "",
  "dir":      None,
  "badyaml":  "profiles:\n  - name: x\n    bogus_field: 1\n",
  "badindent":"profiles:\n- name: x\n  rules:\n  - process: a\n    device: b\n",
  "emptyyaml":"",
  "badtype":  "profiles:\n  - name: x\n    rules:\n      - process: a\n        device: b\n        volume: abc\n",
  "nul":      "profiles:\n\x00\n",
  "big":      None,
}[mode]
if os.path.isdir(path):
    shutil.rmtree(path, ignore_errors=True)
elif os.path.exists(path):
    os.remove(path)
if mode == "dir":
    os.makedirs(path, exist_ok=True)
elif mode == "big":
    lines = ["profiles:", "  - name: big", "    rules:"]
    lines += [f"      - process: p{i}.exe" for i in range(1, 101)]
    lines += [f"        device: D{i}" for i in range(1, 101)]
    open(path, "w", encoding="utf-8").write("\n".join(lines) + "\n")
else:
    open(path, "w", encoding="utf-8", newline="").write(data)
PYEOF
  check "$desc" "$expect" "$@"
}
corrupt "routes.json 垃圾"          any routes.json garbage  ./"$CLI" ctl list
corrupt "routes.json 空文件"        any routes.json empty    ./"$CLI" ctl list
corrupt "routes.json 是目录"        any routes.json dir      ./"$CLI" ctl list
corrupt "routes.json 垃圾→add"      any routes.json garbage  ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID"
corrupt "rules.yaml 未知字段"       any rules.yaml badyaml   ./"$CLI" profile list
corrupt "rules.yaml 缩进错乱"       any rules.yaml badindent ./"$CLI" profile list
corrupt "rules.yaml 空文件"         any rules.yaml emptyyaml ./"$CLI" profile list
corrupt "rules.yaml 类型错误"       any rules.yaml badtype   ./"$CLI" profile list
corrupt "rules.yaml null 字节"      any rules.yaml nul       ./"$CLI" profile list
corrupt "rules.yaml 100 条规则"     any rules.yaml big       ./"$CLI" profile apply --name big --dry-run
restore_files
check "还原后 ctl list"            0   ./"$CLI" ctl list
check "还原后 profile list"        0   ./"$CLI" profile list
check "还原后 profile current"     0   ./"$CLI" profile current

# ============================= 13b. 设备名解析边界 =============================
section "设备名解析边界"
check "--to 单字符(不足2)"         any ./"$CLI" ctl add --pid "$SELF_PID" --to "a"
check "--to 纯空格"                any ./"$CLI" ctl add --pid "$SELF_PID" --to "   "
check "--to 首尾空格合法名"        any ./"$CLI" ctl add --pid "$SELF_PID" --to "  $DEV_ID  "
check "route --to 空串(应拒绝)"    any ./"$CLI" route --pid "$SELF_PID" --to "" --ms 300
check "未知命令退出码=2"           2   ./"$CLI" definitely-not-a-command
fi

# ============================= 13c. 路由表重复路径（手工编辑场景） =============================
section "路由表重复路径（用户手工编辑 routes.json）"
./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID" >/dev/null 2>&1
"$PY" - "$CFG/routes.json" <<'PYEOF'
import sys, json, os
p = sys.argv[1]
if not os.path.isfile(p):
    sys.exit(0)
rows = json.load(open(p, encoding="utf-8"))
if not rows:
    sys.exit(0)
dup = dict(rows[0])
# 同一路径换个大小写 → 旧实现会在 UI 侧 ToDictionary 抛「已存在相同键」
dup["ProcessPath"] = dup["ProcessPath"].upper()
rows.append(dup)
json.dump(rows, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print(f"已写入 {len(rows)} 条（含 1 条重复路径）")
PYEOF
check "重复路径 → ctl list"        0   ./"$CLI" ctl list
check "重复路径 → profile current" 0   ./"$CLI" profile current
check "重复路径 → ctl add"         any ./"$CLI" ctl add --pid "$SELF_PID" --to "$DEV_ID"
restore_files
check "还原后 ctl list"           0   ./"$CLI" ctl list

# ============================= 14. 并发 =============================
if [ "$QUICK" = "0" ]; then
section "并发（各 6 秒）"
run_bg(){ # 描述 + 命令（超时属预期）
  local d="$1"; shift
  local out ec
  out="$(timeout 6 "$@" 2>&1)"; ec=$?
  local st="ok"
  [ "$ec" != "124" ] && [ "$ec" != "0" ] && [ "$ec" != "1" ] && st="EXIT($ec)"
  printf '%s' "$out" | grep -qiE 'unhandled exception|accessviolation|0xc0000005|fatal execution engine|clr20r3|core dumped' && st="CRASH"
  printf '%-54s %-8s ec=%-3s %s\n' "$d" "$st" "$ec" "$(printf '%s' "$out" | tail -1 | cut -c1-44)" | tee -a "$LOG"
  case "$st" in
    ok)    PASS=$((PASS+1)) ;;
    EXIT*) SOFT=$((SOFT+1)) ;;
    CRASH) CRASH=$((CRASH+1)); CRASH_DETAIL="$CRASH_DETAIL
===== $d (ec=$ec) =====
$(printf '%s' "$out" | head -20)" ;;
  esac
}
run_bg "单实例 ctl watch 6s"       ./"$CLI" ctl watch --interval 2000
run_bg "两个 ctl watch 同时 6s"    bash -c './'"$CLI"' ctl watch --interval 2000 & ./'"$CLI"' ctl watch --interval 2000 & wait'
run_bg "watch + add/remove 风暴"   bash -c './'"$CLI"' ctl watch --interval 1000 & for i in $(seq 1 8); do ./'"$CLI"' ctl add --pid '"$SELF_PID"' --to "'"$DEV_ID"'" >/dev/null 2>&1; ./'"$CLI"' ctl remove --pid '"$SELF_PID"' >/dev/null 2>&1; done; wait'
run_bg "8× devices 并发"           bash -c 'for i in $(seq 1 8); do ./'"$CLI"' devices >/dev/null 2>&1 & done; wait'
run_bg "8× sessions 并发"          bash -c 'for i in $(seq 1 8); do ./'"$CLI"' sessions >/dev/null 2>&1 & done; wait'
run_bg "8× proc list 并发"         bash -c 'for i in $(seq 1 8); do ./'"$CLI"' proc list >/dev/null 2>&1 & done; wait'
run_bg "4× ctl add 并发"           bash -c 'for i in $(seq 1 4); do ./'"$CLI"' ctl add --pid '"$SELF_PID"' --to "'"$DEV_ID"'" >/dev/null 2>&1 & done; wait'
fi

# ============================= 汇总 =============================
printf '\n================ 汇总 ================\n' | tee -a "$LOG"
log "通过(优雅) : $PASS"
log "退出码异常 : $SOFT"
log "运行时崩溃: $CRASH"
if [ "$CRASH" -gt 0 ]; then
  printf '%s\n' "$CRASH_DETAIL" | tee -a "$LOG"
  log "完整日志: $LOG"
  exit 1
fi
log "结论：未发现运行时崩溃（CLI 对异常输入均为优雅失败）"
log "完整日志: $LOG"
exit 0
