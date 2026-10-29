#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_app.py  --  LabBorrow 编译脚本（Python 版，与 rebuild.bat 等价）

相比旧版的三点改进：
  1. 不再写死开发机路径，所有路径都相对于本脚本自动推导，换电脑也能直接跑；
  2. 源文件列表自动包含新增的 SwipeGuard.cs / Charts.cs / 各页面文件；
  3. 编译完可选跑自测（SelfTest / UiSmokeTest），保证“现场改代码”不会改坏。

用法：
    python build_app.py              # 编译到发布目录（自动备份原 exe）
    python build_app.py --no-backup  # 不备份
    python build_app.py --test       # 编译后运行规则自测 + 界面冒烟测试
"""
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)                       # 发布包根目录（LabBorrow_Release）
SRC_DIR = HERE                                     # 源码目录 source\
LIB_DIR = os.path.join(ROOT, "_lib")               # 内嵌运行库来源

UHF = os.path.join(LIB_DIR, "UHFReader18CSharp.dll")
BASIC = os.path.join(LIB_DIR, "Basic.dll")
SQLITE = os.path.join(LIB_DIR, "System.Data.SQLite.dll")
MANIFEST = os.path.join(ROOT, "app.manifest")
OUT_EXE = os.path.join(ROOT, "LabBorrow.exe")

CSC_CANDIDATES = [
    r"C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe",
    r"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    r"C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe",
]

# 主程序源文件（顺序无关）
APP_SOURCES = [
    "Program.cs", "Database.cs", "SwipeGuard.cs", "Reader.cs",
    "Charts.cs", "MainForm.cs", "UsersPage.cs", "ReportsPage.cs", "SettingsPage.cs",
]
# 自测源文件（不含界面）
TEST_SOURCES = ["Database.cs", "SwipeGuard.cs", "SelfTest.cs"]
# 界面冒烟测试源文件（含全部界面代码，但不含 Program.cs）
UI_TEST_SOURCES = [f for f in APP_SOURCES if f != "Program.cs"] + ["UiSmokeTest.cs"]

WARN_OFF = "/nowarn:0436;0414;0219;0162;0168;0649;1701;1702;1591;0067"


def find_csc():
    for p in CSC_CANDIDATES:
        if os.path.exists(p):
            return p
    return None


def run(cmd, **kw):
    print("  $", " ".join(cmd) if isinstance(cmd, list) else cmd)
    return subprocess.run(cmd, **kw)


def build_main(csc, no_backup=False):
    if os.path.exists(OUT_EXE) and not no_backup:
        # LabBorrow.exe.bak 只生成一次（保留最初拿到的原始程序，永不覆盖）；
        # 之后每次重新编译，上一版放到 LabBorrow.exe.prev
        bak = OUT_EXE + ".bak"
        if not os.path.exists(bak):
            shutil.move(OUT_EXE, bak)
            print("已保存原始程序备份 ->", bak)
        else:
            prev = OUT_EXE + ".prev"
            if os.path.exists(prev):
                os.remove(prev)
            shutil.move(OUT_EXE, prev)
            print("上一版程序 ->", prev)

    rsp = os.path.join(ROOT, "build.rsp")
    lines = [
        "/nologo", "/target:winexe", "/platform:x86", "/utf8output", "/codepage:65001",
        "/out:" + OUT_EXE,
        "/reference:" + UHF,
        "/reference:" + SQLITE,
        "/reference:System.Drawing.dll",
        "/reference:System.Windows.Forms.dll",
        "/reference:System.Data.dll",
        "/reference:System.dll",
        "/reference:System.Core.dll",
        "/resource:" + UHF + ",LabBorrow.libs.UHFReader18CSharp.dll",
        "/resource:" + BASIC + ",LabBorrow.libs.Basic.dll",
        "/resource:" + SQLITE + ",LabBorrow.libs.System.Data.SQLite.dll",
        WARN_OFF,
    ]
    if os.path.exists(MANIFEST):
        lines.append("/win32manifest:" + MANIFEST)
    for n in APP_SOURCES:
        lines.append(os.path.join(SRC_DIR, n))
    with open(rsp, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    res = run([csc, "@" + rsp])
    if res.returncode != 0:
        print("编译失败，退出码", res.returncode)
        return False
    shutil.copy2(BASIC, os.path.join(ROOT, "Basic.dll"))   # 读卡器运行库需与 exe 同目录
    print("编译成功 ->", OUT_EXE)
    return True


def build_aux(csc, name, sources, main_class, console=True):
    out = os.path.join(ROOT, name + ".exe")
    cmd = [csc, "/nologo", "/target:" + ("exe" if console else "winexe"), "/platform:x86",
           "/utf8output", "/codepage:65001", "/main:" + main_class, "/out:" + out,
           "/reference:" + SQLITE, "/reference:System.Data.dll",
           "/reference:System.dll", "/reference:System.Core.dll", WARN_OFF]
    if not console or main_class.endswith("UiSmokeTest"):
        cmd += ["/reference:" + UHF, "/reference:System.Drawing.dll", "/reference:System.Windows.Forms.dll"]
    cmd += [os.path.join(SRC_DIR, s) for s in sources]
    res = run(cmd)
    if res.returncode != 0:
        print("辅助程序编译失败：", name)
        return None
    return out


def run_tests(csc):
    ok = True
    env = dict(os.environ)

    # ① 规则 / 数据层自测（控制台）
    exe = build_aux(csc, "selftest_tmp", TEST_SOURCES, "LabBorrow.SelfTest")
    if exe:
        test_db = os.path.join(ROOT, "selftest_tmp.db")
        for f in (test_db, test_db + "-journal"):
            if os.path.exists(f):
                os.remove(f)
        env["LABBORROW_DB"] = test_db
        shutil.copy2(SQLITE, ROOT)
        r = subprocess.run([exe], cwd=ROOT, env=env)
        ok = ok and r.returncode == 0
        for f in (exe, test_db, test_db + "-journal"):
            try:
                os.remove(f)
            except OSError:
                pass

    # ② 界面冒烟测试（逐页渲染截图，不需要读卡器）
    exe = build_aux(csc, "uismoke_tmp", UI_TEST_SOURCES, "LabBorrow.UiSmokeTest")
    if exe:
        outdir = os.path.join(ROOT, "ui_smoke")
        r = subprocess.run([exe, outdir], cwd=ROOT, env=env)
        ok = ok and r.returncode == 0
        try:
            os.remove(exe)
        except OSError:
            pass
    return ok


def main():
    csc = find_csc()
    if not csc:
        print("错误：未找到 csc.exe，请安装 .NET Framework 4.0 或更高版本。")
        return 1
    for p in (UHF, BASIC, SQLITE):
        if not os.path.exists(p):
            print("错误：缺少运行库", p)
            return 1

    print("编译器：", csc)
    no_backup = "--no-backup" in sys.argv
    if not build_main(csc, no_backup):
        return 1
    if "--test" in sys.argv:
        print("运行自测 ...")
        if not run_tests(csc):
            print("自测未全部通过，请查看输出与 selftest_report.txt。")
            return 1
        print("自测全部通过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
