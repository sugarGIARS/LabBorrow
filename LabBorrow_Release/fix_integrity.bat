@echo off
chcp 936 >nul
title LabBorrow 完整性级别修复工具
setlocal
set "TARGET=%~dp0."

echo ============================================================
echo    LabBorrow  完整性级别(Integrity Level)修复工具
echo ============================================================
echo.
echo   目标目录 : %TARGET%
echo.
echo   什么情况下需要运行本脚本？
echo     如果本程序被解压到带「低完整性(Low)」标签的目录（常见的
echo     是「桌面」，某些安全软件或沙箱工具会给桌面加这个标签），
echo     那么解压出来的 LabBorrow.exe 会以低完整性级别运行：
echo       · 能正常打开、能看设备列表和历史记录；
echo       · 但新增/编辑/删除设备、刷卡借还、删除记录全部报错
echo         "attempt to write a readonly database"。
echo     因为数据库在 %LOCALAPPDATA%\LabBorrow\ 下，属于中等完整性，
echo     低完整性进程只能读、不能写。
echo.
echo   本脚本把当前目录（含子目录与文件）的完整性级别改回 Medium，
echo   也就是 Windows 的正常级别，之后程序即可正常保存数据。
echo.
echo   即将执行：
echo     icacls "%TARGET%" /setintegritylevel (OI)(CI)Medium /T
echo.
pause

icacls "%TARGET%" /setintegritylevel "(OI)(CI)Medium" /T
if errorlevel 1 goto fail

echo.
echo   [完成] 完整性级别已改回 Medium。
echo          请关闭并重新打开 LabBorrow.exe，然后重试增删改设备。
echo.
pause
exit /b 0

:fail
echo.
echo   [错误] 修改失败。请右键本脚本 -^> 「以管理员身份运行」后重试。
echo.
pause
exit /b 1
