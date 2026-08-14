# 内存释放工具 MemoryCleaner v1.0

一键释放 Windows 物理内存的小工具。通过调用系统 API `EmptyWorkingSet` 清空所有进程的工作集（内存页），让被占用的物理内存交还给系统，缓解内存占用过高导致的卡顿。

**GitHub 仓库：https://github.com/CCGGX-BOT/MemoryCleaner**

**成品 exe：`dist\MemoryCleaner.exe`**（单文件，免安装，依赖系统自带的 .NET Framework 4.x，Win10/Win11 直接双击运行）

---

## 功能

- **实时监控**：每秒刷新物理内存使用率、总内存/已用/可用
- **一键释放**：点击按钮对所有进程执行工作集清理，报告扫描/成功/跳过数量和释放的内存增量
- **进程列表**：按工作集大小排序，实时显示前 50 个最占内存的进程（进程名/PID/工作集/私有内存）
- **自动清理**：勾选后，内存使用率超过阈值（默认 85%，可调 50%~99%）时自动执行清理
- **命令行模式**：支持静默清理，可配合 Windows 任务计划程序定时自动执行

## 使用

### 图形界面

双击 `MemoryCleaner.exe` 打开界面：

1. 点 **「立即释放内存」** 手动清理
2. 或勾选 **「内存使用率超过 _ % 时自动释放」**，让工具在后台自动维护

> 提示：以 **管理员身份运行**（右键 → 以管理员身份运行）可以清理更多进程，效果更好。

### 命令行模式（适合计划任务）

```
MemoryCleaner.exe -c                静默清理一次后退出
MemoryCleaner.exe -t 85             内存使用率超过 85% 才清理，否则直接退出
MemoryCleaner.exe -c -l C:\logs\mem.log   清理并把报告追加写入日志文件
MemoryCleaner.exe -h                显示帮助
```

**示例：设置每天 10:00 自动清理（任务计划程序）**

```
程序或脚本:  C:\path\to\MemoryCleaner.exe
添加参数:    -t 85 -l C:\logs\mem.log
```

## 原理说明

- `EmptyWorkingSet(句柄)`：把指定进程的工作集尽量换出到页面文件，使物理内存空出；进程需要时再按需载回（性能影响很小）
- `SetProcessWorkingSetSize(-1, -1)`：对本工具自身也执行同样操作
- 自动启用 `SeDebugPrivilege` 提升权限，以访问更多系统进程
- 清理的是"工作集"（进程最近用过的物理页），不是退出进程；浏览器等大进程通常能释放数百 MB 到数 GB

## 目录结构

```
MemoryCleaner\
├── dist\MemoryCleaner.exe      ← 成品（直接使用这个）
├── src\MemoryCleaner.cs        源码（C#）
├── src\app.ico                 程序图标
├── src\make_icon.ps1           图标生成脚本
├── build.ps1                   一键编译脚本（需 .NET Framework 4.x 的 csc.exe）
└── upload_to_github.ps1        一键上传到 GitHub 的脚本（纯 API，无需安装 git）
```

重新编译：`powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1`
