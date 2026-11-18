# 双网卡路由配置工具（C# 版）

电脑同时接着两个网络（例如网线连单位内网、Wi-Fi 连互联网）时，Windows 往往会在两块网卡上各放一条默认路由，流量走哪边不确定。本工具把路由表整理成：

- **默认路由只走外网网卡** —— 上互联网走外网；
- **指定的内网网段走内网网卡** —— 访问单位内网、设备网走内网。

本项目是 [MushGrowth/dual-nic-route-tool](https://github.com/MushGrowth/dual-nic-route-tool)（Python 版）的 C# 重写：.NET 10 + WPF，直接调用 Windows IP Helper API 读写路由表。功能设计、路由配置思路和应用图标来自原项目，见[鸣谢](#鸣谢)。

## 目录

- [下载](#下载)
- [运行依赖](#运行依赖)
- [编译为独立 exe](#编译为独立-exe)
- [使用方法](#使用方法)
- [界面说明](#界面说明)
- [命令行参数](#命令行参数)
- [数据存放位置](#数据存放位置)
- [与原 Python 版的差异](#与原-python-版的差异)
- [已知限制](#已知限制)
- [开发与测试](#开发与测试)
- [项目结构](#项目结构)
- [鸣谢](#鸣谢)
- [许可](#许可)

## 下载

从 [Releases](https://github.com/ECoder33/dual-nic-route-tool/releases) 页面下载 exe，双击运行即可，不需要安装。

- 一般用户下载**自包含版**（约 59 MB），电脑上不需要装任何东西。
- 文件名带 `framework-dependent` 的是**依赖框架版**（约 0.7 MB），需要先安装 .NET 10 桌面运行时。

exe 没有代码签名，首次运行时 Windows SmartScreen 可能提示“已保护你的电脑”。请确认文件来自本项目的 Releases 页面，再点“更多信息 → 仍要运行”。

也可以按[编译为独立 exe](#编译为独立-exe)一节自己从源码生成。

## 运行依赖

运行发布出来的 exe 需要：

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 / Windows 11，64 位（x64） |
| 权限 | 管理员。exe 的清单声明了 `requireAdministrator`，启动时会弹出 UAC 提示 |
| .NET 运行时 | 取决于发布方式，见下表 |

| 发布方式 | exe 大小（实测） | 目标电脑需要安装 |
| --- | --- | --- |
| 自包含单文件（推荐分发用） | 约 59 MB | **什么都不需要**，.NET 运行时已打进 exe |
| 依赖框架单文件 | 约 0.7 MB | [.NET 10 桌面运行时（.NET Desktop Runtime 10，x64）](https://dotnet.microsoft.com/download/dotnet/10.0) |

两种方式都只产出一个 `DualNicRouteTool.exe`，没有其他附带文件，可以随意改名、拷到任意目录运行。程序本身不使用任何第三方库。

补充说明：

- 自包含版首次运行时，会把 WPF 的几个原生 DLL 解压到 `%TEMP%\.net\DualNicRouteTool\` 下，所以第一次启动稍慢；该目录被清理后会自动重新解压。
- 依赖框架版在没装运行时的电脑上启动，系统会弹出提示并给出下载链接。
- 只在 Windows 11（x64）上实际运行验证过。Windows 10 在支持范围内但未实测；界面的 Fluent 主题在 Windows 10 上外观会略有不同。

## 编译为独立 exe

### 编译环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 / 11 x64（WPF 项目只能在 Windows 上编译） |
| .NET SDK | 10.0 或更高（本项目用 10.0.404 验证）。用 `dotnet --version` 查看 |
| 网络 | **首次**编译需要能访问 nuget.org：单元测试要下载 xUnit，自包含发布要下载 win-x64 运行时包。下载过一次后会缓存在本机，之后可离线编译 |

不需要 Visual Studio。

### 方式一：用脚本（推荐）

在仓库根目录下打开 PowerShell：

```powershell
.\publish.ps1                           # 先跑单元测试，再生成自包含单文件 exe
.\publish.ps1 -Mode FrameworkDependent  # 生成依赖框架的小体积 exe
.\publish.ps1 -Mode All                 # 两种都生成
.\publish.ps1 -SkipTests                # 跳过单元测试
```

如果系统禁止运行脚本，改用：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

### 方式二：直接用 dotnet 命令

```powershell
# 自包含单文件：目标电脑无需安装 .NET
dotnet publish DualNicRouteTool\DualNicRouteTool.csproj -c Release -p:PublishProfile=SelfContained

# 依赖框架单文件：目标电脑需安装 .NET 10 桌面运行时
dotnet publish DualNicRouteTool\DualNicRouteTool.csproj -c Release -p:PublishProfile=FrameworkDependent
```

### 产物位置

```text
publish\self-contained\DualNicRouteTool.exe
publish\framework-dependent\DualNicRouteTool.exe
```

把其中的 exe 拷走即可使用。

### 发布参数说明

两种发布方式的参数写在 `DualNicRouteTool\Properties\PublishProfiles\` 下的 `.pubxml` 里：

| 参数 | 作用 |
| --- | --- |
| `RuntimeIdentifier=win-x64` | 目标平台。要给 ARM64 设备用可改成 `win-arm64`（未验证） |
| `SelfContained=true` | 把 .NET 运行时一并打包 |
| `PublishSingleFile=true` | 合并成一个 exe |
| `IncludeNativeLibrariesForSelfExtract=true` | 把 WPF 的原生 DLL 也打进 exe，否则 exe 旁边会多出几个 DLL |
| `EnableCompressionInSingleFile=true` | 压缩内嵌的程序集，体积约减半，启动略慢 |

WPF 不支持裁剪（`PublishTrimmed`）和 Native AOT，所以自包含版的体积无法再明显缩小；在意体积请用依赖框架版。

## 使用方法

1. 运行 `DualNicRouteTool.exe`，在 UAC 提示里点“是”。
2. **外网网卡**选能上互联网的那块，**内网网卡**选连接单位内网的那块。
3. 核对两个**网关**。网关会自动填入；内网网卡没有配置网关时需要手动填写，填过一次会被记住。
4. 核对**内网目标网段**，默认是三个私网网段 `10.0.0.0/8`、`172.16.0.0/12`、`192.168.0.0/16`，按实际情况增删。支持 `10.20.0.0/16` 和 `10.20.0.0/255.255.0.0` 两种写法。
5. 点**一键配置**。程序先弹出变更预览，列出将新增、将删除、保持不变的路由，确认后才动手。
6. 要还原时点**恢复原路由**。

配置之后如果发现默认路由又回到了内网网卡上（DHCP 续租、Wi-Fi 重连、休眠唤醒、重新插拔网线都可能触发），在“守护设置”里打开**持续守护**：程序运行期间会定时检查并清理。

## 界面说明

- **颜色**：蓝色代表外网，青绿色代表内网，全界面一致。
- **网卡图标**：下拉列表和卡片右上角的图标表示网卡类型——有线、Wi-Fi、移动宽带、蓝牙、虚拟网卡、VPN / 隧道、拨号。
- **拓扑图**（右上）：直接反映系统当前的路由表，每 3 秒刷新。
  - 彩色线条 + 流动光点：这条路径已生效；
  - 橙色虚线：只生效了一部分（例如还有别的网卡在抢默认路由，或只有部分网段指向内网）；
  - 灰色线条：尚未生效。
- **网段标签**：前面的圆点变成实心，表示该网段的路由已在路由表里。
- **页签**：
  - 操作日志——每一步做了什么，可复制；
  - 当前路由——与本次配置相关的路由（可切换显示全部），标出哪些是本工具添加的；
  - 备份历史——每次配置前的备份，可单独恢复或删除。
- **主题与动效**：明暗主题跟随系统设置。系统里关闭了“动画效果”，或处于远程桌面会话时，界面不播放动画。

## 命令行参数

```text
DualNicRouteTool.exe                  打开图形界面
DualNicRouteTool.exe --debug          输出只读诊断信息（网卡、路由表、备份），不修改任何设置
DualNicRouteTool.exe --debug <文件>   把诊断信息写入指定文件（UTF-8）
DualNicRouteTool.exe --help           显示参数说明
```

诊断信息与界面里“操作日志 → 诊断信息”看到的内容相同。

本程序是窗口程序，在命令行里使用 `--debug` 时注意：

- 请在**管理员**终端里运行。从普通终端运行会先弹 UAC，输出不一定能回到原来的终端；这种情况下程序会改用窗口显示，或者改用 `--debug <文件>`。
- cmd 不会等待窗口程序结束，输出可能出现在下一个提示符之后。需要干净的输出时用 `start /wait DualNicRouteTool.exe --debug`，或者重定向：`DualNicRouteTool.exe --debug > diag.txt`。
- PowerShell 里用管道即可：`.\DualNicRouteTool.exe --debug | Out-String`。

## 数据存放位置

所有数据都在 `%ProgramData%\DualNicRouteTool\`（通常是 `C:\ProgramData\DualNicRouteTool\`）：

| 路径 | 内容 |
| --- | --- |
| `backups\route_backup_<时间>.json` | 每次配置前的路由备份。最多保留 20 份，尚未恢复的备份不会被自动清理 |
| `settings.json` | 上次选择的网卡、手动填写的网关、网段列表、守护设置 |
| `DualNicRouteTool_crash.log` | 程序出现意外错误时的详细信息（平时不存在） |

路由表是整机共享的，所以数据放在 ProgramData 而不是个人目录——换一个管理员账号运行，看到的是同一份备份。

卸载：删除 exe 和上面这个目录即可。程序不写注册表，不安装服务。

## 与原 Python 版的差异

要解决的问题和整体思路与[原项目](https://github.com/MushGrowth/dual-nic-route-tool)相同：外网网卡一条默认路由，内网网段各一条指向内网网关的路由，配置前备份，可一键恢复。下表是两者在实现上的不同之处，供选择版本时参考。

| 方面 | 原 Python 版（V1.1） | 本项目 |
| --- | --- | --- |
| 运行方式 | Python 3.8+ 源码，或 PyInstaller 打包的单文件 exe | .NET 10 单文件 exe |
| 读写路由表 | 解析 `ipconfig /all`、`route print -4` 的输出，调用 `route` 命令 | 调用 IP Helper API（`GetIpForwardTable2` 等）和 .NET 网络接口 API |
| 切换默认路由的顺序 | 先删除现有默认路由，再添加外网默认路由 | 先添加、后删除，全程至少保留一条默认路由 |
| 判断路由属于哪块网卡 | 按网关 IP | 按接口 LUID，两块网卡网关 IP 相同时也能区分 |
| 备份范围 | 默认路由和全部私网段路由 | 默认路由和目标网段上的网关路由；系统自动生成的在链路路由不备份、不改动 |
| 跃点数 | 记录 `route print` 的显示值（路由跃点 + 接口跃点） | 记录并还原路由自身的跃点数 |
| 连续配置多次后恢复 | 回到最近一次配置之前 | 回到第一次配置之前 |
| 恢复后的备份 | 用过即删除 | 保留在“备份历史”里并标记状态；有路由没能还原时可以重试 |
| 出错回滚 | 读取最新的备份文件恢复 | 按内存中的操作记录逐条撤销；备份写不进去就不开始配置 |
| 备份位置 | exe 所在目录 | `%ProgramData%\DualNicRouteTool\backups` |
| 内网网段输入 | 分号分隔的一行文本，无法识别的项跳过 | 逐个添加的标签，输入时校验并规范化（`10.1.2.3/8` → `10.0.0.0/8`） |
| 内网网关 | 需要系统检测到网关 | 可手动填写并记住 |
| 执行前确认 | 点击后直接执行 | 先显示变更预览，确认后执行 |
| 默认路由守护 | 配置后 5 轮、每轮间隔 1.5 秒 | 轮数和间隔可调，另有可选的持续守护 |
| 刷新网卡 | 刷新后需要重新选择 | 保留已选的网卡 |
| 界面 | tkinter | WPF：网卡类型图标、路由拓扑图、明暗主题 |

本项目另外处理的几种情况：

- **已断开网卡的残留路由**：网卡断开后，系统可能仍保留它的默认路由记录（`route print` 不显示，但 API 会返回）。这些记录不参与选路，本工具不把它们当成冲突，也不删除。
- **重启后的旧备份**：本工具添加的是临时路由，重启即消失。开机之前创建的备份会标记为“已过期”，恢复前有明确提示。
- **网卡已不存在**：恢复时遇到所在网卡已断开或被移除的路由，跳过并提示，不算失败。
- **单实例**：同一时间只允许运行一个实例，避免两个实例同时改路由表。
- **远程桌面**：在远程会话里配置时，预览中会警告可能断开连接。

## 已知限制

- 只处理 IPv4 路由。
- 添加的是**非永久路由**，重启电脑后消失，需要重新点一次“一键配置”。这是有意的：配置错了重启就能恢复。
- 持续守护只在程序运行期间有效，关闭程序即停止。
- 网卡类型中，Wi-Fi、移动宽带、隧道、拨号由系统上报，是准确的；蓝牙、虚拟网卡、部分 VPN 是按设备描述里的关键字判断的，个别设备可能识别成“以太网”。这只影响图标，不影响路由配置。
- 备份格式与原 Python 版的 `route_backup_*.json` 不兼容，两个版本的备份不能互相恢复。
- 使用 VPN 时，VPN 客户端自己也会改路由，两者可能互相影响，建议先断开 VPN 再配置。

## 开发与测试

```powershell
dotnet build                                  # 编译整个解决方案
dotnet test                                   # 运行单元测试
dotnet run --project DualNicRouteTool         # 运行（需要在管理员终端里执行）
```

不想提权时，可以绕过 exe 的清单，以只读模式启动（能看网卡和路由表，不能配置）：

```powershell
dotnet DualNicRouteTool\bin\Debug\net10.0-windows\DualNicRouteTool.dll
```

单元测试（`DualNicRouteTool.Tests`，xUnit）只使用内存中的假路由表和假备份存储，**不会读写本机的真实路由**，可以放心运行。覆盖范围：地址与网段解析、变更计划生成、配置 / 回滚 / 守护 / 恢复 / 多次配置合并的完整流程。界面部分没有自动化测试。

代码分层：

- `RoutePlanner` 是纯函数：输入“想要的配置 + 当前路由表”，输出“要加哪些、删哪些、备份哪些”，不做任何系统调用。
- `RouteExecutor` 按计划执行，通过 `IRouteTable`、`IBackupStore` 两个接口访问系统，测试时换成内存实现。
- `NativeRouteTable` 是 `IRouteTable` 的真实实现，所有 P/Invoke 集中在 `Native\IpHelper.cs`。

## 项目结构

```text
.
├─ README.md                      本文档
├─ LICENSE                        MIT 许可（含原项目的版权声明）
├─ publish.ps1                    测试 + 发布脚本
├─ DualNicRouteTool.slnx          解决方案
├─ DualNicRouteTool\              主程序（WPF）
│  ├─ DualNicRouteTool.csproj
│  ├─ app.manifest                管理员权限、DPI 感知声明
│  ├─ App.xaml(.cs)               启动：命令行参数、单实例、异常处理、提权重启
│  ├─ Assets\                     应用图标（来自原项目）
│  ├─ Models\                     数据模型：网段、路由、网卡、计划、备份、设置
│  ├─ Native\                     Windows API 声明（IP Helper、控制台）
│  ├─ Services\                   网卡枚举、路由表读写、计划、执行、备份、诊断、主题
│  ├─ ViewModels\                 界面状态与命令
│  ├─ Views\                      主窗口、变更预览、提示框、使用说明、诊断窗口
│  ├─ Controls\                   矢量图标控件、动效、路由拓扑图
│  ├─ Themes\                     明 / 暗配色、图标集、控件样式
│  └─ Properties\PublishProfiles\ 两种发布方式的参数
└─ DualNicRouteTool.Tests\        单元测试（xUnit）
```

## 鸣谢

本项目是 [MushGrowth/dual-nic-route-tool](https://github.com/MushGrowth/dual-nic-route-tool) 的 C# 重写，感谢原作者 [MushGrowth](https://github.com/MushGrowth) 的工作。以下内容来自原项目：

- 要解决的问题和整体方案：外网默认路由 + 内网专用路由、配置前备份、失败回滚、一键恢复、DHCP 默认路由守护；
- 默认的内网网段和主要的操作流程；
- 应用图标（`DualNicRouteTool\Assets\` 下的 `app_icon.ico`、`app_icon.png`）。

代码部分为重新编写，由 [ECoder33](https://github.com/ECoder33) 与 Claude（Anthropic 的 AI 助手）共同完成。

## 许可

[MIT](LICENSE)。原项目同为 MIT 许可，其版权声明保留在 `LICENSE` 中。
