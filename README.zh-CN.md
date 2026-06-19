# VComTunnel

[English](README.md) | [简体中文](README.zh-CN.md)

VComTunnel 是一个面向嵌入式开发和实验室设备调试的虚拟串口桥接工具。它让
Windows 上的串口终端、烧录工具、调试工具继续打开本地 `COMx`，然后由本机
服务把数据和串口控制信号转发到远端 RFC2217 设备。

典型场景是 ESP-DAP 或其它网络串口设备已经提供 `rfc2217://host:port`，
但上位机工具仍然只认识本地 COM 口。VComTunnel 负责把这两边接起来，并把
映射配置、后台服务、日志、依赖诊断和 COM 口管理做成一个可操作的桌面工具。

## 当前状态

这个项目目前有三条后端路径：

- `com0comHub4com`：稳定基线。使用 com0com 创建一对虚拟串口，再用
  hub4com/com2tcp-rfc2217 连接 RFC2217 目标。优先推荐这条路径。
- `com0comService`：过渡路径。仍然使用 com0com 提供可见 COM 口，但
  RFC2217 协议由 VComTunnel.Service 自己处理，不再经过 hub4com 进程。
- `kmdf`：实验路径。使用项目里的 KMDF 虚拟串口驱动直接提供 COM 口，
  理论上不再需要 com0com。这个后端还不是生产级签名驱动，只建议在测试机
  上验证。

如果目标是可靠烧录、日志和日常使用，先使用 `com0comHub4com`。如果目标是
验证最终的一步到位虚拟 COM 到 RFC2217，则可以在测试环境里试 `kmdf`。

## 工作方式

```text
串口工具 -> 本地 COMx -> VComTunnel 后端 -> VComTunnel.Service -> RFC2217 host:port
```

三种后端的链路分别是：

```text
com0comHub4com:
串口工具 -> COMx -> com0com -> CNCBx -> hub4com/com2tcp-rfc2217 -> RFC2217

com0comService:
串口工具 -> COMx -> com0com -> CNCBx -> VComTunnel.Service -> RFC2217

kmdf:
串口工具 -> COMx -> VComTunnel.Serial.sys -> VComTunnel.Service -> RFC2217
```

GUI 只是控制台和状态面板。映射启动以后可以由后台服务继续运行，关闭 GUI
不会自动停止隧道。

## 快速开始

开发环境构建：

```powershell
dotnet build VComTunnel.sln
dotnet run --no-build --project tests\VComTunnel.Tests\VComTunnel.Tests.csproj
```

启动 GUI：

```powershell
dotnet run --project src\VComTunnel.Gui\VComTunnel.Gui.csproj
```

如果只想调试本机 API/服务：

```powershell
dotnet run --project src\VComTunnel.Service\VComTunnel.Service.csproj -- --console
```

常用 CLI：

```powershell
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- diagnose
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- deps install
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- status
dotnet run --project src\VComTunnel.Cli\VComTunnel.Cli.csproj -- mappings
```

## 安装依赖

阶段 1 的稳定后端依赖两个第三方组件：

- com0com：创建 Windows 可见的虚拟串口对。
- hub4com：把 com0com 的另一端接到 RFC2217 目标。

VComTunnel 的依赖安装功能会优先使用发布包里的离线归档。如果归档不存在，
开发构建会尝试下载官方 SourceForge 包：

- hub4com 2.1.0.0:
  <https://sourceforge.net/projects/com0com/files/hub4com/2.1.0.0/hub4com-2.1.0.0-386.zip/download>
- com0com 3.0.0.0 signed package:
  <https://sourceforge.net/projects/com0com/files/com0com/3.0.0.0/com0com-3.0.0.0-i386-and-x64-signed.zip/download>
- com0com files index:
  <https://sourceforge.net/projects/com0com/files/>

hub4com 解压后即可使用。com0com 是 Windows 驱动，仍然需要用户批准管理员
权限安装；VComTunnel 可以准备安装包并拉起安装器，但不会绕过 Windows 驱动
安装和签名策略。

GUI 里的 `Setup deps` 会完成依赖准备、诊断刷新和 com0com 安装器拉起。CLI
也可以执行同样的动作：

```powershell
vcomtunnelctl deps install
vcomtunnelctl deps launch-com0com
```

## 创建和删除 COM 口

`com0comHub4com` 和 `com0comService` 都需要一对 com0com 端口：

```text
Visible COM = COM27
Backing     = CNCB27
```

`Visible COM` 是给 esptool、串口终端、IDE 等工具打开的端口；`Backing` 是
VComTunnel 或 hub4com 消费的另一端。

GUI 的 COM 端口面板可以查看、创建和删除 com0com pair。CLI 可以生成计划：

```powershell
vcomtunnelctl create-hints
vcomtunnelctl pair create-plan <mappingId>
vcomtunnelctl pair remove-plan <pairNumber>
```

实际创建/删除 com0com 端口需要管理员确认，因为它会调用 com0com 的
`setupc.exe` 修改驱动设备。

`kmdf` 后端没有 `CNCB` 这一侧。它只有一个可见 COM 口，例如 `COM25`，
服务通过驱动私有通道收发数据。

## 后台服务

VComTunnel.Service 监听本机 `127.0.0.1:44817`，GUI 和 CLI 都通过这个本机
API 管理映射。

安装 Windows 后台服务：

```powershell
vcomtunnelctl service install C:\Tools\VComTunnel\VComTunnel.Service.exe
vcomtunnelctl service start
```

卸载或停止：

```powershell
vcomtunnelctl service stop
vcomtunnelctl service uninstall
```

映射里的 `autoStart` 表示服务启动时自动启动该隧道；`restartOnFailure`
表示网络断开或进程退出后尝试重连。

## 发布安装文件

当前仓库已经提供发布脚本，输出的是一个可分发目录和 `.zip` 包：

```powershell
scripts\package-release.ps1 -Version 0.1.0 -Runtime win-x64
```

发布机如果可以访问 NuGet，可以加 `-Restore`：

```powershell
scripts\package-release.ps1 -Version 0.1.0 -Runtime win-x64 -Restore
```

如果要做可复现的离线发布，提前准备第三方依赖归档，并传入缓存目录：

```powershell
scripts\package-release.ps1 -Version 0.1.0 -Runtime win-x64 -DependencyArchiveRoot C:\Deps\VComTunnel
```

发布包会包含：

- GUI、Service、CLI 的发布产物。
- `dependencies` 目录下的 com0com/hub4com 归档。
- `THIRD-PARTY-NOTICES.txt`。
- `SHA256SUMS.txt`。

这个脚本目前产出的是 zip 形态的安装包素材，不是完整 MSI/MSIX 安装器。
后续如果要给普通用户分发，建议再补一个安装器层：

- WiX/MSI 或 Inno Setup：适合安装 GUI、CLI、Service、开始菜单快捷方式。
- Windows Service：安装器里调用 `vcomtunnelctl service install`。
- com0com：保留独立驱动安装确认，不静默绕过。
- KMDF：生产发布前必须解决正式驱动签名，测试签名驱动不能作为普通用户安装包
  分发。

## KMDF 驱动

KMDF 驱动代码在 `drivers\VComTunnel.Serial`。它的目标是让 VComTunnel 自己
提供虚拟串口，不再依赖 com0com/hub4com。

构建和安装测试驱动需要 WDK、测试签名环境和管理员权限。相关脚本：

```powershell
drivers\VComTunnel.Serial\build-driver.ps1
drivers\VComTunnel.Serial\install-test-driver.ps1
```

注意：测试签名驱动只适合开发验证。正式发布需要走 Microsoft 驱动签名流程，
否则普通 Windows 机器无法按生产方式安装。

## RFC2217 验证

可以使用 smoke 工具直接探测 RFC2217 endpoint：

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- --probe-rfc2217 10.0.2.196 5000
```

安全探测设置 ACK：

```powershell
dotnet run -c Release --project tools\VComTunnel.Smoke\VComTunnel.Smoke.csproj -- --probe-rfc2217 10.0.2.196 5000 --probe-settings --probe-query
```

只有确认目标设备可以被复位或扰动时，才使用 `--probe-controls`，因为 DTR、
RTS、BREAK、purge 等控制可能影响连接的开发板。

## 安全边界

- 本机 API 只应监听 `127.0.0.1`。
- RFC2217 本身没有 TLS 和认证，默认只适合可信局域网或实验室网络。
- 不要把 VComTunnel 的 API 或 RFC2217 设备直接暴露到不可信网络。
- 驱动、COM 口创建和控制线时序都可能影响真实硬件，升级前先用安全目标验证。

## 贡献

欢迎围绕串口工具兼容性、RFC2217 互操作、诊断、打包、文档和跨平台 GUI
继续完善。提交前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可证

VComTunnel 使用 MIT License 发布，详见 [LICENSE](LICENSE)。
