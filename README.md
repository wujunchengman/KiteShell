# KiteShell

> 一个用 AI 写的跨平台 SSH 会话管理与终端工具

在 Windows 上用惯了 MobaXterm 那种「SSH 终端 + SFTP 文件面板」混排的体验，但 Linux 上没有趁手的替代品，于是就有了 KiteShell。

项目基于 **Avalonia + C# + SSH.NET**，可跨平台运行

本项目提供 **Native AOT** 发布：启动快、体积小、不依赖目标机器安装 .NET 运行时。

## 功能特性

### 会话管理

- 树形会话列表，支持无限层级分组、搜索、重命名、复制与删除
- 顶栏快速连接：输入主机、用户名、端口即可直接连接
- 会话支持密码 / 私钥认证，可保存端口、启动命令、强调色，并记录最近连接时间
- 一键从 `~/.ssh/config` 导入主机（自动跳过通配 `Host` 与 `Match` 块）

### 终端

- 多标签页，每个标签独立会话，支持断线重连与手动重连
- 自绘终端控件：256 色 / TrueColor、ANSI 转义序列解析、备用屏幕缓冲区
- 5000 行滚动历史，支持鼠标滚轮、拖动滚动条、`Shift+PageUp/PageDown`
- 鼠标框选、双击选词、`Ctrl+Shift+C / V` 复制粘贴
- 中文宽字符与输入法（IME）候选框定位
- `Ctrl+Shift+= / -` 调整字号，`Ctrl+Shift+0` 恢复默认

### SFTP 文件面板

- 与终端使用独立连接，互不阻塞
- 浏览目录、路径跳转、上级目录、刷新
- 支持从文件管理器**拖拽文件或文件夹上传**，远端目录自动创建
- 下载、新建目录、重命名、删除

### 安全

- 首次连接校验主机指纹（TOFU），指纹变更时弹窗告警
- 保存的密码使用 **AES-GCM** 加密，密钥文件权限限制为当前用户可读写
- 会话与配置以 JSON 落盘，写入使用临时文件 + 原子替换，损坏时自动保留 `.bak`

### 其他

- 内置快捷命令（Snippets），可自行增删，双击即可发送到当前终端
- Linux 下检测到 `WAYLAND_DISPLAY` 时优先使用 Wayland 原生后端
- GitHub Actions 自动构建 Windows / Linux x64 的 Native AOT 包

## 下载与安装

前往仓库的 **Releases** 页面下载对应平台的压缩包：

| 平台 | 文件 | 说明 |
| --- | --- | --- |
| Linux x64 | `KiteShell-linux-x64.tar.gz` | 解压后运行 `./KiteShell` |
| Windows x64 | `KiteShell-win-x64.zip` | 解压后运行 `KiteShell.exe` |

> Linux 包在 Ubuntu 22.04 上构建，需要 glibc 2.35+ 的发行版。首次运行前如无执行权限，请先 `chmod +x KiteShell`。

## 从源码构建

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 仅发布 Native AOT 时需要：
  - **Windows**：Visual Studio 2022 Build Tools，勾选「使用 C++ 的桌面开发」
  - **Linux**：`clang`、`zlib1g-dev`

  ```bash
  sudo apt install clang zlib1g-dev
  ```

### 调试运行

```bash
git clone <仓库地址>
cd KiteShell
dotnet run
```

### 发布 Native AOT 单文件包

```bash
# Linux
dotnet publish KiteShell.csproj -c Release -r linux-x64 --self-contained -o publish/linux-x64

# Windows
dotnet publish KiteShell.csproj -c Release -r win-x64 --self-contained -o publish/win-x64
```

产物位于 `publish/<rid>/`，可直接拷贝到目标机器运行。

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+Shift+N` | 新建会话 |
| `Ctrl+Shift+F` | 新建分组 |
| `Ctrl+Shift+W` | 关闭当前标签页 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 切换到下一个 / 上一个标签页 |
| `Ctrl+Shift+C` 或 `Ctrl+Insert` | 复制选中文本 |
| `Ctrl+Shift+V` 或 `Shift+Insert` | 粘贴 |
| `Ctrl+Shift+=` / `Ctrl+Shift+-` | 放大 / 缩小终端字号 |
| `Ctrl+Shift+0` | 重置终端字号 |
| `Shift+PageUp` / `Shift+PageDown` | 向上 / 向下翻页 |
| `Ctrl+Shift+Home` / `Ctrl+Shift+End` | 跳到历史顶部 / 底部 |

## 数据存放位置

| 平台 | 目录 |
| --- | --- |
| Windows | `%APPDATA%\KiteShell` |
| Linux | `~/.config/KiteShell` |

| 文件 | 内容 |
| --- | --- |
| `sessions.json` | 会话树与连接配置 |
| `known_hosts.json` | 已信任的主机指纹 |
| `snippets.json` | 快捷命令 |
| `secret.key` | 本地加密密钥（请勿泄露或提交到仓库） |

> 保存密码只做本机范围的静态加密，不等同于系统级密钥环（Keyring）。在共享账号或不可信环境中，建议不要勾选保存密码。

## 项目结构

```
KiteShell/
├── Models/          # 会话节点、远端文件项、快捷命令等数据模型
├── Services/        # 会话存储、SSH/SFTP、主机指纹、加密、ssh config 导入
├── Terminal/        # ANSI 解析、终端缓冲区、终端控件、按键编码
├── ViewModels/      # 会话树节点视图模型
├── Views/           # 标签页、会话编辑、主机指纹与输入弹窗
├── MainWindow.axaml # 主界面：会话树 + 标签终端 + 状态栏
├── App.axaml        # 主题、样式与图标资源
└── Program.cs       # 应用入口（含 Wayland 后端选择）
```

## 技术栈

- [.NET 10](https://dotnet.microsoft.com/) + C#
- [Avalonia 12](https://avaloniaui.net/)（Fluent 主题、Inter 字体、Wayland 后端）
- [SSH.NET](https://github.com/sshnet/SSH.NET) 提供 SSH / SFTP 能力
- `System.Text.Json` 源生成序列化，兼容 Native AOT

## 参与贡献

欢迎提交 Issue 与 Pull Request。提交前请确保：

1. `dotnet build` 无警告与错误；
2. 涉及终端的改动在 Windows 与 Linux 上手动验证基本操作；
3. 遵循现有代码风格与中文注释习惯。

## 致谢

- 本项目目前由**吃白饭的大肥鱼**提供主要技术支持。
- 感谢 [Avalonia](https://avaloniaui.net/)、[SSH.NET](https://github.com/sshnet/SSH.NET) 等开源项目。

## 许可证

本项目基于 [GNU General Public License v3.0](LICENSE) 开源发布。

Copyright (C) 2026 吴俊城

你可以自由使用、修改和分发本项目，但衍生作品必须以相同协议开源，且不提供任何形式的担保。详见 [LICENSE](LICENSE)。
