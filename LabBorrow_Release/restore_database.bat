@echo off
chcp 936 >nul
title LabBorrow 数据库恢复工具
setlocal
set "SRC=%~dp0data\labborrow.db"
set "DSTDIR=%LOCALAPPDATA%\LabBorrow"
set "DST=%DSTDIR%\labborrow.db"

echo ============================================================
echo    LabBorrow  实验室设备借还登记程序 - 数据库恢复工具
echo ============================================================
echo.
echo   源数据库 : %SRC%
echo   目标位置 : %DST%
echo.
echo   说明：本程序把数据固定存放在上面的「目标位置」，
echo         因此换电脑或重装系统后，需要运行本脚本把随包
echo         数据库复制过去，才能看到原有的设备与借还记录。
echo         若目标位置已有数据库，会先自动备份、不会被删除。
echo.

if not exist "%SRC%" goto nosrc

tasklist /fi "imagename eq LabBorrow.exe" 2>nul | find /i "LabBorrow.exe" >nul
if not errorlevel 1 (
  echo   [警告] 检测到 LabBorrow.exe 正在运行。
  echo          请先关闭程序，否则数据库被占用、复制会失败。
  echo.
)

if not exist "%DSTDIR%" mkdir "%DSTDIR%"
if not exist "%DST%" goto copydb

rem 已有数据库：先做非破坏性备份（.bak / .bak1 / .bak2 ...），再覆盖
set "BAK=%DST%.bak"
if not exist "%BAK%" goto dobackup
set "i=1"
:findbak
set "BAK=%DST%.bak%i%"
if exist "%BAK%" (set /a i+=1 & goto findbak)
:dobackup
copy /y "%DST%" "%BAK%" >nul
if errorlevel 1 (
  echo   [错误] 备份原数据库失败，已中止，未做任何修改。
  echo.
  pause
  exit /b 1
)
echo   [已备份] 原数据库保存为：
echo            %BAK%
echo.

:copydb
copy /y "%SRC%" "%DST%" >nul
if errorlevel 1 goto copyfail
echo   [完成] 数据库已恢复到：
echo          %DST%
echo.
echo   现在可以运行 LabBorrow.exe，查看原有设备与借还记录。
echo.
pause
exit /b 0

:nosrc
echo   [错误] 未找到 data\labborrow.db
echo          请确认压缩包已完整解压后再运行本脚本。
echo.
pause
exit /b 1

:copyfail
echo   [错误] 复制失败，目标文件可能正被占用。
echo          请先关闭正在运行的 LabBorrow.exe，然后重试。
echo.
pause
exit /b 1
