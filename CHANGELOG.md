# Changelog

## 1.0.0 — 2026-11-22

- 首次发布。
- 开机时以 SYSTEM 计划任务在登录前开启 Windows 移动热点。
- 启动后每 10 秒检查一次，持续 60 秒，确认热点稳定后自动退出，不常驻后台。
- 支持“系统启动”与 Kernel-General 12 事件双触发器，兼容快速启动（Fast Startup）场景。
- 图形界面：状态、SSID、已连接设备数、共享网络、安装/卸载开机自启。
- 命令行：`run` / `on` / `off` / `status` / `install` / `uninstall`。
- 使用 Windows 自带 `NetworkOperatorTetheringManager`，不依赖第三方运行库。
