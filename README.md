# Quick-Share-PC

<div align="center">

# ⚡ Quick Share (Windows 桌面端)

**极速、轻量、高吞吐的 Windows 局域网文件传输服务端 / 客户端**

[![.NET](https://img.shields.io/badge/.NET-7.0%20WPF-512BD4.svg?logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011-blue.svg?logo=windows)](https://microsoft.com/windows)
[![Protocol](https://img.shields.io/badge/Protocol-QuickShare%20v300-orange.svg)](#传输协议)
[![Build](https://img.shields.io/badge/Build-Passing-brightgreen.svg)](#编译与构建)

</div>

---

## 📖 项目简介

**Quick-Share-PC** 是一款基于 C# 与 .NET 7 WPF 构建的 Windows 桌面局域网高速文件传输工具。与手机端 [Quick-Share-Android](https://github.com/naruse-love/Quick-Share-Android) 深度互通，实现 PC 与手机之间免流量、免数据线、高带宽的大文件与批量文件夹极速互传。

本项目彻底移除了以往多物理网卡绑定切片的繁琐设置，聚焦于**纯局域网极致单流吞吐优化**，提供清晰直观的本地网络状态展示、一键复制 IP、托盘后台运行及流式磁盘异步写入，轻松跑满千兆网卡与高速 SSD。

---

## ✨ 核心特性

- 🚀 **纯局域网高速流式传输**：采用高效单连接 TCP 数据管道，针对局域网大文件与文件夹传输深度优化，无外网依赖。
- 🖥️ **现代化轻量桌面界面**：
  - **网络状态卡片**：自动枚举并高亮显示本机当前主局域网 IPv4 地址与适配器类型，提供一键“📋 复制 IP”按钮。
  - **服务灵活管理**：支持自定义服务监听端口（默认 `5740`），一键启动/停止服务。
  - **实时传输监控**：实时仪表盘显示传输总进度、瞬时传输速度、已传输/总字节数。
- 📦 **大文件与文件夹无损传输**：
  - 递归遍历多层级子目录，完整保留源端目录树结构与文件修改时间戳（Last Modified Time）。
  - 支持 64 位大文件切片流式读写，预分配目标文件尺寸，杜绝磁盘碎片。
- ⚡ **高性能零 GC 缓冲池**：
  - 8×1MB 预分配内存缓冲队列（`ArrayBlockingQueue` / `BlockingCollection`），流式复用内存，避免高吞吐传输引发的 GC 停顿。
- 🔔 **系统托盘与便捷交互**：
  - 支持窗口最小化到系统托盘，后台持续稳定传输。
  - 支持拖拽文件/文件夹直接发起传输。

---

## 🏗️ 目录与架构设计

```
Quick-Share-PC/
├── QuickSharePC/               # WPF 主工程源码
│   ├── Models/                 # 数据模型
│   │   ├── AppConfig.cs        # 端口、下载目录、配置持久化
│   │   ├── FileBlock.cs        # 1MB 切片数据块实体
│   │   ├── QuickShareDirectory.cs # 跨平台路径归一化与转换
│   │   ├── RemoteFile.cs       # 远程文件/目录元数据
│   │   └── NetworkInterfaceInfo.cs # 网络接口信息
│   ├── Services/               # 核心业务与网络引擎
│   │   ├── QuickShareServer.cs # 局域网协议握手、指令解析与会话管理
│   │   ├── QuickShareConstants.cs # 协议常量与大端流编解码
│   │   ├── ReadFileCall.cs     # 目录递归遍历与流式分块读取
│   │   ├── WriteFileCall.cs    # 高速流式消费写入与时间戳恢复
│   │   ├── NetworkService.cs   # 本机局域网 IP 与网卡枚举
│   │   ├── ConfigService.cs    # JSON 配置文件读写
│   │   └── TrayService.cs      # 系统托盘图标与右键菜单
│   ├── ViewModels/             # MVVM 视图模型
│   │   └── MainViewModel.cs    # 界面状态绑定、指令调度、测速与进度更新
│   ├── MainWindow.xaml         # 现代化 WPF 主窗口界面
│   └── App.xaml.cs             # 应用程序入口与全局异常捕获
├── QuickSharePC.EmpiricalTests/# 实证与对抗测试套件
└── README.md
```

---

## 🛠️ 编译与构建

### 环境要求
- **操作系统**：Windows 10 / 11 (x64 / ARM64)
- **开发环境**：Visual Studio 2022 或 VS Code
- **.NET SDK**：[.NET 7.0 SDK](https://dotnet.microsoft.com/download/dotnet/7.0) 或更高版本

### 编译与运行
```powershell
# 1. 进入 PC 工程目录
cd QuickSharePC

# 2. 编译项目 (Debug)
dotnet build

# 3. 发布独立 Release 可执行程序
dotnet publish -c Release -r win-x64 --self-contained false -o ../publish
```
编译生成的程序可直接运行 `Quick-Share-PC.exe`。

---

## 📲 与 Android 手机互联指南

1. **启动 PC 服务端**：
   - 打开 `Quick-Share-PC`，在主界面确认“监听端口”（默认 `5740`），点击“启动服务”。
   - 界面“网络状态”将显示本机的局域网 IP（例如 `192.168.1.100`），点击“复制”即可复制 IP。
2. **手机端连接**：
   - 在手机端打开 `Quick-Share-Android`。
   - 在“连接”页面输入上述 PC 的 IP 地址与端口，点击“连接”。
3. **开始互传**：
   - 在手机端或 PC 端选择要发送的文件/文件夹，点击发送，两端将以最大局域网速度流式互传，实时显示传输进度与速率。

---

## 📄 开源许可证

本项目遵循 [MIT License](LICENSE) 开源。
