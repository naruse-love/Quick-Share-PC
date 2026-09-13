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
- 🔄 **双向对等双模架构（服务端 + 客户端）**：
  - **🖥️ 服务端模式**：监听指定端口（默认 `5740`），等待手机端或对端 PC 连接，被动接收或主动推送文件。
  - **📱 客户端模式**：支持主动连接至远端设备（手机端服务端模式或另一台 PC 服务端），无需依赖固定主从关系。
  - **无缝切换**：现代化分段控件（Segmented Switch）一键秒切模式，自动保存对端 IP 与端口配置。
- 🖥️ **现代化轻量桌面界面**：
  - **网络状态卡片**：自动枚举并高亮显示本机当前主局域网 IPv4 地址与适配器类型，提供一键“📋 复制 IP”按钮。
  - **服务与连接管理**：服务端支持自定义监听端口；客户端支持快速配置目标 IP 与端口，并展示远端设备信息。
  - **实时传输监控**：实时仪表盘显示传输总进度、瞬时传输速度、已传输/总字节数。
- 📦 **大文件与文件夹无损传输**：
  - 递归遍历多层级子目录，完整保留源端目录树结构与文件修改时间戳（Last Modified Time）。
  - 支持 64 位大文件切片流式读写，预分配目标文件尺寸，杜绝磁盘碎片。
- ⚡ **高性能零 GC 缓冲池**：
  - 8×1MB 预分配内存缓冲队列（`ArrayBlockingQueue` / `BlockingCollection`），流式复用内存，避免高吞吐传输引发的 GC 停顿。
- 🔔 **系统托盘与便捷交互**：
  - 支持窗口最小化到系统托盘，后台持续稳定传输。
  - 统一拖拽传输卡片，支持将文件/文件夹拖入窗口自动根据当前连接状态路由发送。

---

## 🏗️ 目录与架构设计

```
Quick-Share-PC/
├── QuickSharePC/               # WPF 主工程源码
│   ├── Converters/             # XAML 绑定值转换器
│   │   ├── InverseBooleanConverter.cs # 布尔反转转换器
│   │   └── StatusColorConverter.cs    # 连接与运行状态颜色映射
│   ├── Models/                 # 数据模型
│   │   ├── AppConfig.cs        # 端口、目标 IP/Port、模式、下载目录持久化
│   │   ├── FileBlock.cs        # 1MB 切片数据块实体
│   │   ├── QuickShareDirectory.cs # 跨平台路径归一化与转换
│   │   ├── RemoteFile.cs       # 远程文件/目录元数据
│   │   └── NetworkInterfaceInfo.cs # 网络接口信息
│   ├── Services/               # 核心业务与网络引擎
│   │   ├── QuickShareServer.cs # 服务端协议握手、指令解析与会话管理
│   │   ├── QuickShareClient.cs # 客户端主动连接、RPC 通信与文件传输引擎
│   │   ├── QuickShareConstants.cs # 协议常量与大端流编解码
│   │   ├── ReadFileCall.cs     # 目录递归遍历与流式分块读取
│   │   ├── WriteFileCall.cs    # 高速流式消费写入与时间戳恢复
│   │   ├── NetworkService.cs   # 本机局域网 IP 与网卡枚举
│   │   ├── ConfigService.cs    # JSON 配置文件读写
│   │   └── TrayService.cs      # 系统托盘图标与右键菜单
│   ├── ViewModels/             # MVVM 视图模型
│   │   └── MainViewModel.cs    # 服务端/客户端双模调度、指令绑定、测速与进度更新
│   ├── MainWindow.xaml         # 现代化 WPF 主窗口界面（双模分段切换与统一传输卡片）
│   └── App.xaml.cs             # 应用程序入口、生命周期管理与全局异常捕获
├── QuickSharePC.EmpiricalTests/# 实证与对抗测试套件（33 项全面协议测试）
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

# 3. 运行完整实证测试套件 (33 项)
dotnet run --project ../QuickSharePC.EmpiricalTests

# 4. 发布独立 Release 可执行程序
dotnet publish -c Release -r win-x64 --self-contained false -o ../publish
```
编译生成的程序可直接运行 `Quick-Share-PC.exe`。

---

## 📲 互联互传指南

### 场景一：PC 作为服务端（手机连接 PC）
1. **PC 端**：打开 `Quick-Share-PC`，顶部切换至 **🖥️ 服务端模式**，点击“启动服务”。
2. **手机端**：打开 `Quick-Share-Android`，进入客户端连接页面，输入 PC 界面显示的局域网 IP 与端口，点击“连接”。
3. **互传**：连接成功后，任一端选择或拖入文件/文件夹即可秒速流式互传。

### 场景二：PC 作为客户端（PC 连接手机或其他 PC）
1. **远端设备**：打开手机端的“服务端模式”或者另一台 PC 的“服务端模式”，启动服务并查看其 IP 与端口。
2. **PC 端**：打开 `Quick-Share-PC`，顶部切换至 **📱 客户端模式**，输入远端的 IP 地址和端口，点击“连接”。
3. **互传**：连接建立后，拖拽文件到 PC 传输卡片或点击“选择文件发送”，数据即通过局域网直连管道高速发送至远端。

---

## 📄 开源许可证

本项目遵循 [MIT License](LICENSE) 开源。
