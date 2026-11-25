@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "HotspotAuto.exe" (
    echo 找不到 HotspotAuto.exe。
    echo 请把本文件和 HotspotAuto.exe 放在同一个文件夹里。
    pause
    exit /b 1
)
echo 正在安装开机自启（如果弹出 UAC，请点“是”）...
"HotspotAuto.exe" install
echo.
pause
