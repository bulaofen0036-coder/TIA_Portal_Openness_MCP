# -*- coding: utf-8 -*-
"""TIA VCI 看门狗 —— 博途程序一变，就自动导出文本、写 change log、git 提交。

一次运行 = 一个周期，跑完就退出（由计划任务或 loop.cmd 反复调用）。
这样即使某次卡死，也不会留下长期占着博途的进程。

铁律（改这个脚本前先读完）：
1. **绝不打开博途、绝不打开工程**。博途没在跑，或跑着但没开工程 → 直接安静退出。
   工程师的工程只能 Attach，这条是红线（历史上因为 Connect+CreateProject 关过用户工程）。
2. **绝不往工程里写**。只做 ProjectToWorkspace 方向的同步（写文本文件），
   永远不调 WorkspaceToProject，也不调 SaveProject —— 存不存盘是工程师的决定。
3. **只提交有实质变更的**。没有变更就一个字都不输出、不产生空提交。
4. 日志落 log/，每轮追加；失败不抛给用户，写日志并以非 0 退出码结束。
"""

import datetime
import io
import json
import os
import queue
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG = os.path.join(HERE, "config.json")
LOGDIR = os.path.join(HERE, "log")


def log(msg):
    os.makedirs(LOGDIR, exist_ok=True)
    # 文件名每轮重算：长跑脚本里用启动时算好的日期，跨天会把全部记录堆进首日文件
    path = os.path.join(LOGDIR, "watch-%s.log" % datetime.datetime.now().strftime("%Y%m%d"))
    line = "[%s] %s" % (datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), msg)
    with io.open(path, "a", encoding="utf-8") as f:
        f.write(line + "\n")
    # pythonw.exe（计划任务用它来避免弹黑窗）下 sys.stdout 是 None，print 会抛异常
    if sys.stdout is not None:
        try:
            print(line)
        except Exception:
            pass


# Windows：从无控制台的父进程(pythonw)启动控制台程序，会给每个子进程新开一个黑窗。
# 每一处 Popen/run 都必须带上它，漏一个就闪一个。
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0x08000000)

LOCK = os.path.join(HERE, "watch.lock")
STATE = os.path.join(HERE, "watch.state.json")


def ps(cmd):
    """跑一段 PowerShell，返回 stdout。"""
    p = subprocess.run(["powershell", "-NoProfile", "-Command", cmd],
                       capture_output=True, text=True, encoding="utf-8", errors="replace",
                       creationflags=NO_WINDOW)
    return (p.stdout or "").strip()


def kill_pid(pid, why):
    if not pid:
        return
    ps("Stop-Process -Id %d -Force -ErrorAction SilentlyContinue" % int(pid))
    log("已清理进程 %s（%s）" % (pid, why))


def read_state():
    try:
        return json.load(io.open(STATE, encoding="utf-8"))
    except Exception:
        return {}


def write_state(d):
    io.open(STATE, "w", encoding="utf-8").write(json.dumps(d, ensure_ascii=False))


def cleanup_stale(engine_exe):
    """清掉上一轮卡死没退干净的引擎。只认我们自己记下的 PID + 命令行匹配，
    绝不按进程名批量杀 —— 用户别的 Claude 会话跑的是同一个 exe。"""
    st = read_state()
    pid = st.get("enginePid")
    if not pid:
        return
    cmd = ps("(Get-CimInstance Win32_Process -Filter \"ProcessId=%d\" -EA SilentlyContinue).CommandLine" % int(pid))
    if cmd and engine_exe.lower() in cmd.lower() and "tia-vci-watch" not in cmd.lower():
        # 命令行对得上，说明这个 PID 还是上一轮那个引擎（没被系统重用）
        kill_pid(pid, "上一轮残留的引擎")
    st = read_state()
    st.pop("enginePid", None)
    write_state(st)


def kill_orphan_tia(engine_pid):
    """绝不留下由我们引擎拉起的博途实例。只杀父进程是我们引擎的那些，
    用户自己开的 GUI（父进程是 explorer）绝不碰。

    杀之前先把 PID 记进 state：计划任务的 ExecutionTimeLimit（15 分钟）短于最坏一轮，
    整个 python 被掐死时 finally 跑不到，无头实例就留下来白占内存了（实测有过一个
    88MB 的残留挂了一整天）。记下来，下一轮 sweep_recorded_tia() 补杀。"""
    out = ps("Get-CimInstance Win32_Process -Filter \"Name='Siemens.Automation.Portal.exe'\" | "
             "Where-Object { $_.ParentProcessId -eq %d } | Select-Object -Expand ProcessId" % int(engine_pid))
    pids = [int(l.strip()) for l in out.splitlines() if l.strip().isdigit()]
    if not pids:
        return
    st = read_state()
    st["spawnedTiaPids"] = sorted(set(st.get("spawnedTiaPids", []) + pids))
    write_state(st)
    for pid in pids:
        kill_pid(pid, "本轮引擎自己拉起的博途，不该留下")
    st = read_state()
    st["spawnedTiaPids"] = [p for p in st.get("spawnedTiaPids", []) if p not in pids]
    write_state(st)


def sweep_recorded_tia():
    """补杀上一轮记下、但没来得及清掉的无头博途。

    只杀命令行确实是 Openness 无头实例的（-bootstrapper=…Openness.Loader.BootStrapper）——
    PID 会被系统重用，光凭 PID 下手可能误伤别的进程；工程师的 GUI 命令行里没有
    -bootstrapper，天然不会命中。"""
    st = read_state()
    pids = st.get("spawnedTiaPids") or []
    if not pids:
        return
    for pid in list(pids):
        cmd = ps("(Get-CimInstance Win32_Process -Filter \"ProcessId=%d\" -EA SilentlyContinue).CommandLine" % int(pid))
        if cmd and "siemens.automation.portal.exe" in cmd.lower() and "openness.loader.bootstrapper" in cmd.lower():
            kill_pid(pid, "上一轮没清干净的无头博途")
    st = read_state()
    st.pop("spawnedTiaPids", None)
    write_state(st)


class Engine(object):
    """把 MCP 引擎当 stdio 子进程驱动。"""

    def __init__(self, exe, tia_version):
        self.p = subprocess.Popen(
            [exe, "--tia-major-version", str(tia_version), "--profile", "full", "--logging", "0"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0,
            creationflags=NO_WINDOW)
        # 降到 BelowNormal：看门狗是后台活，不许和用户正在操作的博途抢 CPU
        try:
            ps("(Get-Process -Id %d).PriorityClass = 'BelowNormal'" % self.p.pid)
        except Exception:
            pass
        self.id = 0
        self.q = queue.Queue()
        threading.Thread(target=self._reader, daemon=True).start()
        self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                  "clientInfo": {"name": "tia-vci-watch", "version": "1"}})
        self._send("notifications/initialized", {}, notify=True)

    def _reader(self):
        buf = b""
        while True:
            ch = self.p.stdout.read(1)
            if not ch:
                self.q.put(None)
                return
            if ch == b"\n":
                line = buf.decode("utf-8", "replace").strip()
                buf = b""
                if line:
                    try:
                        self.q.put(json.loads(line))
                    except ValueError:
                        pass
            else:
                buf += ch

    def _send(self, method, params=None, notify=False, timeout=600):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            self.id += 1
            msg["id"] = self.id
        self.p.stdin.write((json.dumps(msg) + "\n").encode("utf-8"))
        self.p.stdin.flush()
        if notify:
            return None
        want = self.id
        deadline = time.time() + timeout
        while time.time() < deadline:
            m = self.q.get(timeout=timeout)
            if m is None:
                raise RuntimeError("引擎进程退出")
            if m.get("id") == want:
                return m
        raise RuntimeError("等待 %s 超时" % method)

    def call(self, name, **kwargs):
        r = self._send("tools/call", {"name": name, "arguments": kwargs})
        if "error" in r:
            raise RuntimeError("%s: %s" % (name, json.dumps(r["error"], ensure_ascii=False)))
        texts = [c["text"] for c in r.get("result", {}).get("content", []) if c.get("type") == "text"]
        raw = "\n".join(texts)
        try:
            return json.loads(raw)
        except ValueError:
            return {"message": raw, "items": []}

    def close(self):
        try:
            self.p.terminate()
        except Exception:
            pass


def sh(args, cwd=None):
    p = subprocess.run(args, cwd=cwd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace", creationflags=NO_WINDOW)
    return p.returncode, (p.stdout or "").strip(), (p.stderr or "").strip()


def user_tia_sessions():
    r"""工程师自己开着的博途实例数。**不是** Portal.exe 的进程数。

    一台机器上同时可能跑三种 Portal.exe，只有第一种算数：
      1. 工程师双击 .apXX 开的 GUI —— 命令行里只有工程路径，没有 -bootstrapper。
      2. 那个 GUI 自己拉起的后台辅助进程（HMICompiler / SearchReplaceBackgroundProcess）——
         带 -bootstrapper=…BackgroundProcessBootStrapper 和 -processId=<GUI 的 PID>。
      3. 别的 Openness 客户端拉起的无头实例 —— 带
         -bootstrapper=Siemens.Automation.Opns.Loader.Impl:…Openness.Loader.BootStrapper。

    按进程数判断会把 2 和 3 也算成"博途开着"：实测一个开着工程的 GUI 就是 3 个进程，
    而一台没开任何工程、只剩一个无头残留的机器同样数到 1。后者最坏 —— 看门狗判定
    "博途开着"就起引擎，引擎又找不到能附着的工程，转而自己再拉起一个无头实例，
    600 秒超时被掐断，留下的残留让下一轮继续为真：整夜每 72 分钟空转一次。
    带 -bootstrapper 的一律不算，剩下的就是真 GUI 会话。
    """
    _, out, _ = sh(["powershell", "-NoProfile", "-Command",
                    "@(Get-CimInstance Win32_Process -Filter \"Name='Siemens.Automation.Portal.exe'\""
                    " -EA SilentlyContinue | Where-Object { $_.CommandLine -notlike '*-bootstrapper=*' }).Count"])
    try:
        return int(out.strip() or "0")
    except ValueError:
        return 0


def has_open_project(eng):
    """在 Connect 之前问一句：现在有没有哪个博途进程真开着工程。

    ListPortalProcessProjects 只探测已在跑的进程，自己不会拉起博途；Connect 会。
    没工程就直接收工，别让 Connect 去"帮忙"开一个 —— 那正是无头残留的来源。
    行形如 `PID=49452 project=MyProject_V21` / `PID=48004 projects=<empty>`，
    所以要匹配单数 ` project=`，`projects=` 是"没有"。
    """
    try:
        r = eng.call("ListPortalProcessProjects")
    except Exception:
        return True                   # 探测不了就别拦，交给后面的 Attach 判
    return any(" project=" in it for it in (r.get("items") or []))


def block_names(items):
    """状态行形如 'S7-1200 station_5_测试PLC_程序块_A3_6 | Unequal | file=… | format=…'，取块名。"""
    out = []
    for it in items:
        name = it.split("|")[0].strip()
        out.append(name.split("_")[-1] or name)
    return out


def write_changelog(ws, changed, status_items):
    path = os.path.join(ws, "CHANGELOG.md")
    stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    body = ["", "## %s" % stamp, ""]
    for it in status_items:
        parts = [p.strip() for p in it.split("|")]
        obj = parts[0] if parts else it
        state = parts[1] if len(parts) > 1 else "?"
        body.append("- `%s` — %s" % (obj, "工程侧有改动" if state == "Unequal" else state))
    body.append("")
    head = ""
    if not os.path.exists(path):
        head = "# 博途程序变更记录\n\n由 TIA VCI 看门狗自动生成：每次检测到工程里的块与文本文件不一致，\n就把它导出并记在这里。**不含硬件组态**（VCI 不支持）。\n"
    with io.open(path, "a", encoding="utf-8") as f:
        if head:
            f.write(head)
        f.write("\n".join(body))
    return path



def project_signal(project_folder):
    r"""返回工程目录里"被动过"的最新时间戳。只扫会被博途编辑/编译写到的几处，
    刻意**不扫 Vci\**（那是我们自己同步写的，扫了会自触发）。"""
    if not project_folder or not os.path.isdir(project_folder):
        return None
    newest = 0.0
    targets = [project_folder,
               os.path.join(project_folder, "XRef"),
               os.path.join(project_folder, "IM", "SearchIndex"),
               os.path.join(project_folder, "System")]
    for d in targets:
        try:
            with os.scandir(d) as it:
                for e in it:
                    if e.name.lower() == "vci":       # 我们自己的地盘，跳过
                        continue
                    try:
                        newest = max(newest, e.stat().st_mtime)
                    except OSError:
                        pass
        except OSError:
            continue
    return newest or None


def main():
    cfg = json.load(io.open(CONFIG, encoding="utf-8"))
    exe = cfg["enginePath"]
    tia_version = cfg.get("tiaMajorVersion", 21)
    ws = cfg["workspaceFolder"]
    ws_name = cfg.get("workspaceName", "")
    author = cfg.get("gitAuthor", "tia-vci-watch <watch@local>")

    if not user_tia_sessions():
        return 0                      # 工程师没开博途 —— 安静退出，什么都不做

    # 廉价前置检查：工程目录没被动过就秒退，连引擎都不启动。
    # 兜底：超过 forceFullCheckMinutes 无论如何全量查一次，不把启发式当唯一依据。
    st_prev = read_state()
    sig = project_signal(cfg.get("projectFolder", ""))
    last_sig = st_prev.get("lastSignal")
    last_full = st_prev.get("lastFullCheck", 0)
    since_full = time.time() - last_full
    force_after = cfg.get("forceFullCheckMinutes", 60) * 60
    unchanged = (sig is not None and last_sig is not None and abs(sig - last_sig) < 0.001)

    # a) 工程没被动过 → 秒退（除非到了兜底全量时间）
    if unchanged and since_full < force_after:
        return 0

    # b) 两次完整周期至少隔这么久。有变更的周期可能长达数分钟，
    #    2 分钟一轮会首尾相接、等于一直挂在博途上。
    if since_full < cfg.get("minFullCheckMinutes", 10) * 60:
        return 0

    # c) 待编译退避：上一轮的失败全是"块未编译"时，在工程被动过之前不再反复重试 ——
    #    未编译的块状态不会自己变好，每轮重试只是白白连博途、让界面闪。
    if unchanged and st_prev.get("pendingCompile"):
        if time.time() < st_prev.get("pendingUntil", 0):
            return 0

    # 单实例：上一轮还没跑完就跳过这一轮，绝不叠加（10 分钟一轮 × 一轮可能 90 秒）
    if os.path.exists(LOCK):
        try:
            age = time.time() - os.path.getmtime(LOCK)
        except OSError:
            age = 0
        if age < cfg.get("lockTimeoutSeconds", 900):
            return 0
        log("发现超期的锁（%d 秒），按卡死处理" % age)
    io.open(LOCK, "w", encoding="utf-8").write(str(os.getpid()))

    timeout_s = cfg.get("cycleTimeoutSeconds", 600)
    deadline = time.time() + timeout_s
    _did_something = [False]
    _killed = [False]
    _pending = [None, 0]        # (待编译块名摘要, 冷却截止时间)
    eng = None

    def _reaper():
        """到点掐断：真的杀引擎，而不是只记一句"已超时"。"""
        while time.time() < deadline:
            time.sleep(2)
            if _finished[0]:
                return
        if eng is not None and eng.p.poll() is None:
            _killed[0] = True
            log("本轮超过 %d 秒，按卡死掐断引擎（不让它占着博途和内存）" % timeout_s)
            kill_pid(eng.p.pid, "超时掐断")

    _finished = [False]
    threading.Thread(target=_reaper, daemon=True).start()
    try:
        eng = Engine(exe, tia_version)
        cleanup_stale(exe)
        sweep_recorded_tia()
        _st = read_state()
        _st.update({"enginePid": eng.p.pid, "startedAt": time.time()})
        write_state(_st)
        # Connect 之前先确认真有工程可搭 —— 见 has_open_project()
        if not has_open_project(eng):
            return 0

        eng.call("Connect")
        # 只 Attach，绝不 Open：工程是工程师开的，我们只是搭个车
        att = eng.call("AttachToOpenProject")
        if not att.get("meta", {}).get("success", True):
            log("没有已打开的工程可附着：%s" % att.get("message", ""))
            return 0

        st = eng.call("GetVersionControlStatus", changedOnly=True, workspaceName=ws_name)
        items = st.get("items") or []
        if not items:
            return 0                  # 无变更 —— 不出声、不产生空提交

        _did_something[0] = True
        log("检测到 %d 个对象有变更：%s" % (len(items), "、".join(block_names(items)[:8])))

        syn = eng.call("SyncVersionControlWorkspace", direction="ProjectToWorkspace",
                       dryRun=False, workspaceName=ws_name)

        # 失败分两类。"块不一致 → 先编译"是常态而非故障：工程师刚在博途里改完、还没编译时
        # 必然是这个状态，VCI 明确拒绝导出（The block is inconsistent. Compile the block prior to export.）
        fails = [it for it in (syn.get("items") or []) if "FAILED" in it]
        need_compile = [it for it in fails if "inconsistent" in it]
        hard_fails = [it for it in fails if it not in need_compile]

        if need_compile:
            names = "、".join(block_names(need_compile)[:8])
            if cfg.get("autoCompile", False):
                log("%d 个块改了但未编译，按配置自动编译：%s" % (len(need_compile), names))
                for sw in cfg.get("compileSoftwarePaths", []):
                    r = eng.call("CompileSoftware", softwarePath=sw)
                    log("  编译 %s → %s" % (sw, r.get("message", "")))
                syn = eng.call("SyncVersionControlWorkspace", direction="ProjectToWorkspace",
                               dryRun=False, workspaceName=ws_name)
                fails = [it for it in (syn.get("items") or []) if "FAILED" in it]
                need_compile = [it for it in fails if "inconsistent" in it]
                hard_fails = [it for it in fails if it not in need_compile]
            else:
                cool = cfg.get("pendingCompileCooldownMinutes", 30)
                _pending[0] = names
                _pending[1] = time.time() + cool * 60
                log("%d 个块改了但**尚未编译**，VCI 导不出：%s" % (len(need_compile), names))
                log("  → 在博途里编译一次即可；在那之前暂停重试 %d 分钟，避免反复连博途" % cool)

        log("导出：%s" % syn.get("message", ""))
        for it in hard_fails:
            log("  失败明细：%s" % it.replace("\r", " ").replace("\n", " "))
        if hard_fails:
            log("有非「待编译」的失败项，本轮不提交，留给人看")
            return 1

        # VCI 说 Unequal 不等于"文本真的变了"：git checkout / pull 把文件原样重写一遍
        # （内容相同、时间戳变）同样会被判 Unequal。只看 git 有没有真差异，
        # 否则会刷出一串只含 CHANGELOG 的空提交。
        _, porcelain, _ = sh(["git", "status", "--porcelain"], cwd=ws)
        real = [l for l in porcelain.splitlines()
                if l.strip() and not l.split(" ")[-1].endswith("CHANGELOG.md")]
        if not real:
            log("导出后文本无实际差异（VCI 判 Unequal 多半只是文件被重写过），不提交")
            return 0

        write_changelog(ws, len(items), items)

        name, email = author.split("<")[0].strip(), author.split("<")[1].rstrip(">")
        sh(["git", "add", "-A"], cwd=ws)
        code, out, err = sh(["git", "-c", "user.name=%s" % name, "-c", "user.email=%s" % email,
                             "commit", "-m",
                             "auto: %d 个对象变更 —— %s" % (len(real), "、".join(block_names(items)[:6]))],
                            cwd=ws)
        if code == 0:
            log("已提交：%s" % (out.splitlines()[0] if out else "已提交"))
        else:
            log("git commit 未成功：%s %s" % (out[:200], err[:200]))
        return 0
    except Exception as e:
        log("本轮失败：%r" % (e,))
        return 1
    finally:
        _finished[0] = True
        if eng:
            mem = ps("[math]::Round((Get-Process -Id %d -EA SilentlyContinue).WorkingSet64/1MB)" % eng.p.pid)
            over = _killed[0] or time.time() > deadline
            kill_orphan_tia(eng.p.pid)
            eng.close()
            try:
                eng.p.wait(timeout=10)
            except Exception:
                kill_pid(eng.p.pid, "引擎没有正常退出")
            noisy = over or _did_something[0]
            try:
                noisy = noisy or (mem and int(mem) > 300)
            except (TypeError, ValueError):
                pass
            if noisy:
                log("本轮结束：引擎峰值内存约 %s MB%s" % (mem or "?", "，**已超时**" if over else ""))
        # 整份覆盖会把 kill_orphan_tia 刚记下的 spawnedTiaPids 一起抹掉 —— 那正是
        # 用来在下一轮补杀没清干净的无头博途的，必须原样带过去。
        _final = read_state()
        _final.update({"lastSignal": project_signal(cfg.get("projectFolder", "")),
                       "lastFullCheck": time.time(),
                       "pendingCompile": _pending[0],
                       "pendingUntil": _pending[1]})
        _final.pop("enginePid", None)
        _final.pop("startedAt", None)
        write_state(_final)
        try:
            os.remove(LOCK)
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(main())
