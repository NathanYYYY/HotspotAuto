@echo off
chcp 65001 >nul
cd /d "%~dp0"
if not exist "HotspotAuto.exe" (
    echo 找不到 HotspotAuto.exe。
    pause
    exit /b 1
)
echo 正在卸载开机自启（如果弹出 UAC，请点“是”）...
"HotspotAuto.exe" uninstall
echo.
pause
