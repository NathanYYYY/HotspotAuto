@echo off
chcp 65001 >nul
net session >nul 2>&1
if errorlevel 1 (
    echo 需要管理员权限，正在请求...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 1 /f >nul
if errorlevel 1 (
    echo 修改失败，请重试。
) else (
    echo 已恢复快速启动（Windows 默认设置）。
)
pause
