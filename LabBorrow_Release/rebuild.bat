@echo off
chcp 936 >nul
rem =====================================================================
rem  rebuild.bat  --  LabBorrow 一键重新编译（用 .NET Framework 自带编译器）
rem  ---------------------------------------------------------------------
rem  现场需要改代码时：改完 source\*.cs，双击本脚本即可在当前目录生成新的
rem  LabBorrow.exe（原 exe 自动备份为 LabBorrow.exe.bak）。
rem  编译后会依次运行：
rem    · 规则/数据层自测（自测报告 selftest_report.txt）
rem    · 界面冒烟测试（把 6 个页面渲染成 ui_smoke\*.png，可肉眼确认没画坏）
rem  两者都不需要连接读卡器。
rem =====================================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [错误] 未找到 csc.exe，请先安装 .NET Framework 4.0 或更高版本。
  exit /b 1
)
if not exist "_lib\UHFReader18CSharp.dll" (
  echo [错误] 找不到 _lib\UHFReader18CSharp.dll，请在完整发布包目录中运行本脚本。
  exit /b 1
)

set SRC=source
set WARNOFF=/nowarn:0436;0414;0219;0162;0168;0649;1701;1702;1591;0067
set COMMONREF=/reference:_lib\System.Data.SQLite.dll /reference:System.Data.dll /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll

echo [1/5] 生成编译参数 build.rsp ...
> build.rsp echo /nologo
>> build.rsp echo /target:winexe
>> build.rsp echo /platform:x86
>> build.rsp echo /utf8output
>> build.rsp echo /codepage:65001
>> build.rsp echo /out:LabBorrow.exe
>> build.rsp echo /reference:_lib\UHFReader18CSharp.dll
>> build.rsp echo /reference:_lib\System.Data.SQLite.dll
>> build.rsp echo /reference:System.Drawing.dll
>> build.rsp echo /reference:System.Windows.Forms.dll
>> build.rsp echo /reference:System.Data.dll
>> build.rsp echo /reference:System.dll
>> build.rsp echo /reference:System.Core.dll
>> build.rsp echo /reference:System.Security.dll
>> build.rsp echo /win32manifest:app.manifest
>> build.rsp echo /resource:_lib\UHFReader18CSharp.dll,LabBorrow.libs.UHFReader18CSharp.dll
>> build.rsp echo /resource:_lib\Basic.dll,LabBorrow.libs.Basic.dll
>> build.rsp echo /resource:_lib\System.Data.SQLite.dll,LabBorrow.libs.System.Data.SQLite.dll
>> build.rsp echo %WARNOFF%
for %%f in (Program.cs Database.cs SwipeGuard.cs Reader.cs NfcReader.cs Kiosk.cs Mailer.cs Sound.cs Charts.cs Reminder.cs RemindDialog.cs MainForm.cs UsersPage.cs ReportsPage.cs SettingsPage.cs) do >> build.rsp echo %SRC%\%%f

echo [2/5] 备份原程序 ...
rem  LabBorrow.exe.bak 只生成一次（保留最初拿到的原始程序，永不覆盖）；
rem  之后每次重新编译，上一版放到 LabBorrow.exe.prev。
if exist LabBorrow.exe (
  if not exist LabBorrow.exe.bak (
    move /y LabBorrow.exe LabBorrow.exe.bak >nul
    echo        已保存原始程序备份 -^> LabBorrow.exe.bak
  ) else (
    if exist LabBorrow.exe.prev del /q LabBorrow.exe.prev
    move /y LabBorrow.exe LabBorrow.exe.prev >nul
  )
)

echo [3/5] 编译主程序 ...
"%CSC%" @build.rsp
if errorlevel 1 (
  echo [失败] 编译出错，请查看上面的错误信息。
  if exist LabBorrow.exe.prev move /y LabBorrow.exe.prev LabBorrow.exe >nul
  if not exist LabBorrow.exe if exist LabBorrow.exe.bak move /y LabBorrow.exe.bak LabBorrow.exe >nul
  exit /b 1
)
copy /y _lib\Basic.dll Basic.dll >nul
echo        成功 -^> %CD%\LabBorrow.exe

echo [4/5] 规则与数据层自测 ...
"%CSC%" /nologo /target:exe /platform:x86 /utf8output /codepage:65001 /main:LabBorrow.SelfTest ^
  /out:selftest_tmp.exe %COMMONREF% %WARNOFF% ^
  %SRC%\Database.cs %SRC%\SwipeGuard.cs %SRC%\Mailer.cs %SRC%\SelfTest.cs >nul 2>nul
if errorlevel 1 (
  echo        [警告] 自测程序编译失败，建议检查 source\SelfTest.cs
) else (
  copy /y _lib\System.Data.SQLite.dll . >nul 2>nul
  mkdir _selftest_tmp 2>nul
  selftest_tmp.exe "%CD%\_selftest_tmp\selftest.db" >nul
  if errorlevel 1 (
    echo        [警告] 自测未全部通过，详见 selftest_report.txt
  ) else (
    echo        自测全部通过
  )
  del /q selftest_tmp.exe 2>nul
  rmdir /s /q _selftest_tmp 2>nul
)

echo [5/5] 界面冒烟测试（逐页截图，不需要读卡器）...
"%CSC%" /nologo /target:exe /platform:x86 /utf8output /codepage:65001 /main:LabBorrow.UiSmokeTest ^
  /out:uismoke_tmp.exe %COMMONREF% /reference:_lib\UHFReader18CSharp.dll ^
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll %WARNOFF% ^
  %SRC%\Database.cs %SRC%\SwipeGuard.cs %SRC%\Reader.cs %SRC%\NfcReader.cs %SRC%\Kiosk.cs %SRC%\Mailer.cs ^
  %SRC%\Sound.cs %SRC%\Charts.cs ^
  %SRC%\Reminder.cs %SRC%\RemindDialog.cs %SRC%\MainForm.cs ^
  %SRC%\UsersPage.cs %SRC%\ReportsPage.cs %SRC%\SettingsPage.cs %SRC%\UiSmokeTest.cs >nul 2>nul
if errorlevel 1 (
  echo        [警告] 界面冒烟测试程序编译失败
) else (
  uismoke_tmp.exe ui_smoke >nul
  if errorlevel 1 (
    echo        [警告] 界面冒烟测试未全部通过
  ) else (
    echo        界面冒烟测试全部通过，截图见 ui_smoke\
  )
  del /q uismoke_tmp.exe 2>nul
)

del /q System.Data.SQLite.dll 2>nul

echo.
echo [完成] 新程序：%CD%\LabBorrow.exe
echo        原程序备份：%CD%\LabBorrow.exe.bak
endlocal
