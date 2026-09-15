# freedom WinUI 3

基于 [v2rayN](https://github.com/2dust/v2rayN) 使用 WinUI 3 重构的 Windows 代理客户端。

本项目专注于 Windows 桌面体验，支持 Xray、sing-box、Mihomo 等代理核心，并修复了一些安装、更新、TUN 和系统代理相关问题。

## 主要功能

- WinUI 3 原生 Windows 界面
- 支持系统代理和 TUN 模式
- 支持路由规则、订阅和节点管理
- 支持通过系统代理检查和下载更新
- 支持自动退出应用并覆盖安装新版本
- 安装升级时保留原有节点、设置、日志和数据库

## 下载

软件更新地址为 [GitHub Releases](https://github.com/qhs200312/freedom-winui/releases/latest)。
匿名自动更新需要仓库和 Release 可公开访问；客户端不内置 GitHub 凭证。

| 文件 | 适用设备 | 说明 |
| --- | --- | --- |
| `freedom-windows-64-setup.exe` | Windows x64 | 包含运行环境和代理核心，推荐使用 |
| `freedom-windows-64.zip` | Windows x64 | 便携版和应用内更新包 |
| `freedom-windows-arm64.zip` | Windows ARM64 | ARM64 便携版和应用内更新包 |

本项目仅发布 Windows 版本，不提供 Linux 或 macOS 版本。

安装器会请求管理员权限，默认选中 UDP 接管依赖安装任务，内含 ProxiFyre 2.6.1、
Windows Packet Filter 3.6.2.1 x64 和 VC++ 运行库。驱动安装可能短暂中断网络或要求重启。
freedom 每次启动都通过应用清单请求管理员权限，与 UDP 接管开关无关。
Windows 仍可能显示 UAC 确认，拒绝授权则程序不启动，不会修改功能开关。
管理员启动不会替代驱动安装，也不会绕过 Windows 授权。
便携版不自动安装驱动。卸载客户端不删除共享驱动和运行库。

## 工程结构

唯一界面工程为 `v2rayN/v2rayN.WinUI`。解决方案还保留必需的 `ServiceLib`、
`ServiceLib.UdpTest`、退出恢复助手 `AmazTool` 和 `ServiceLib.Tests`。
WPF、Avalonia 及其专用热键工程不再参与构建。

使用 .NET 10 和 Windows SDK，在 `v2rayN` 目录构建：

```powershell
dotnet build v2rayN.WinUI.slnf -c Debug -p:Platform=x64
dotnet test ServiceLib.Tests/ServiceLib.Tests.csproj
```

“设置 → 参数 → 隐私保护”的“STUN 服务器优先走代理”默认开启。
旧配置首次升级会启用一次，之后手动关闭的选择会被保留。
定位联动作用于当前 Windows 用户，停止代理后恢复原策略；STUN 分流需要
TUN 接管和支持 UDP 的节点，不保证覆盖全部 WebRTC 泄漏场景。

STUN 分流优先于普通分流规则：sing-box 匹配 STUN 协议或已知 STUN 域名，
不再仅凭 3478 等端口代理任意 UDP。Xray 启动/重载时解析已知 STUN 域名，
以解析到的 IP 加 STUN 端口进行匹配，并保留域名匹配；解析失败会提示。
局域网、链路本地、组播和广播地址不加入这组代理目标，其他 UDP 仍走原路由。
Xray 的地址集合不能保证覆盖浏览器使用其他 DNS、旧缓存或自定义 STUN 服务的情况。
此功能不等于完整的 WebRTC 媒体流代理，也不会拦截未进入内核的流量。

## 系统要求

- Windows 10 2004（版本 19041）或更高版本
- x64 或 ARM64 处理器

## 升级说明

从 7.23.8 开始，粘贴订阅地址并导入后会自动更新对应的已启用订阅，不会更新无关订阅。
出口地区优先使用中文查询结果，并对常见国家、地区和城市提供离线中文名称；
未覆盖的地名会保留原始名称，不编造翻译。

在“组件更新”中检查并更新 freedom 后，客户端会校验下载包的 SHA-256，退出旧进程，
由独立临时目录中的助手替换安装文件，然后重新启动。配置、订阅数据库和日志不会被覆盖；
文件替换失败时会尝试回滚。升级日志位于安装目录的 `guiLogs/freedom-update.log`。
升级助手和原安装程序使用相同权限，不绕过 Windows UAC。

维护者在新仓库推送版本标签（例如 `v7.23.9`）即可触发 Release 构建，
生成 `freedom-windows-64.zip`、对应的 `.sha256` 和安装包。
仓库保持私有时，客户端的匿名更新无法读取 Release；不要将个人 GitHub 令牌写入客户端。

安装包必须来自自包含 `dotnet publish --self-contained true` 输出，不能直接打包
`dotnet build` 目录。`installer/build-installer.ps1` 会验证主程序、退出助手的运行时声明，
以及 .NET Desktop、WinUI、代理核心和 UDP 依赖文件；验证失败即停止打包。

安装器以 freedom 显示，保留原安装 AppId，并识别 freedom / v2rayN 的安装目录进行覆盖升级。
升级过程中会保留 `guiConfigs`、日志和数据库，建议重要配置仍定期自行备份。
旧配置目录、WebDAV 默认备份目录、内部分享协议及仓库地址保留兼容，不因品牌更名而迁移。

## 上游项目

本项目基于 [2dust/v2rayN](https://github.com/2dust/v2rayN) 开发。协议、核心支持范围及基础使用文档可参考[上游 Wiki](https://github.com/2dust/v2rayN/wiki)。
