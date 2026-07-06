<div align="center">

<img src="docs/logo.png" width="140" alt="OpenWire logo" />

# OpenWire

**开源的 Windows 网络监控 + 应用防火墙。**

[English](README.md) · **简体中文** · [繁體中文](README.zh-Hant.md)

</div>

OpenWire 是对 GlassWire 式桌面体验的净室（clean-room）开源重实现：实时滚动的带宽图表、
按进程归因的流量统计、双向应用防火墙、世界连接地图、局域网设备扫描、带异常检测的用量
分析，以及网络安全警报引擎——完全免费，无账号、无遥测、无付费墙。

> OpenWire 是独立项目，**与 GlassWire / SecureMix LLC 无任何隶属或背书关系，也不包含其
> 任何源代码**。它基于公开文档与可观察行为构建，代码与资源全部原创。

![流量图](docs/screenshots/traffic-pencil.png)

---

## 亮点

- **实时流量图** — 平滑滚动、自动缩放的下载/上传面积图，时间范围从 5 分钟到一个月。
  在图上横向拖拽即可框选任意时间段查看精确总量；拖动底部时间轴的圆形拖柄即可缩放，
  视图全帧率跟手。
- **按进程归因** — 精确到每个应用的收发流量，带真实图标、发布者、每应用火花线和子进程
  列表。基于 **ETW** 采集，无内核驱动。
- **应用防火墙** — 通过 Windows Defender 防火墙双向允许/拦截任意应用，支持**按网络自动
  切换的配置档**（Wi-Fi SSID / 网关指纹识别）以及新应用的"连接询问"弹窗。
- **世界连接地图** — 可缩放的分级设色地图 + **3D 地球**双视图，点击国家可下钻查看主机。
- **筛选面板** — GlassWire 式侧栏，按应用/主机/流量类型/国家地区列出用量，支持
  WAN/LAN、方向与搜索过滤。
- **分析 + 异常检测** — 小时/每日用量规律、流量最高应用、人话要点，自动检测流量尖峰、
  重上传应用、新国家与异常时段活动；另有 hosts 文件与 ARP 欺骗完整性监控。
- **警报** — 新应用、新设备、DNS 变更、入站 RDP、流量套餐阈值、用量异常——分组、可筛
  选、可搜索。
- **扫描器** — 盘点局域网内每台设备（IP、MAC、厂商、类型、首次发现、在线状态）。
- **硬件资源** — CPU / 内存 / 磁盘 / GPU 实时曲线，与流量图同款平滑滚动。
- **四套主题即时热切换** — 简约（扁平）、铅笔素描（手绘）、草莓日间/夜间（痛车风），
  外加 **English / 简体中文 / 繁體中文** 三语界面，全部无需重启。
- **VirusTotal 集成** — 可选，使用你自己的免费 API key 做哈希查询（只有 SHA-256 离开
  本机）。
- **纯本地设计** — 无账号、无云端、无遥测，历史数据存于本地 SQLite。

## 主题

| 铅笔素描 | 简约 |
|---|---|
| ![Pencil](docs/screenshots/traffic-pencil.png) | ![Minimal](docs/screenshots/traffic-minimal.png) |

| 草莓日间 | 草莓夜间 |
|---|---|
| ![Berry day](docs/screenshots/traffic-berry-day.png) | ![Berry night](docs/screenshots/traffic-berry-night.png) |

## 更多界面

| 世界地图 | 3D 地球 |
|---|---|
| ![2D 世界地图](docs/screenshots/map.png) | ![3D 地球](docs/screenshots/globe.png) |

| 硬件资源 | 设置 |
|---|---|
| ![硬件](docs/screenshots/hardware.png) | ![设置](docs/screenshots/settings.png) |

---

## 架构

OpenWire 由两个进程组成——提权后台引擎 + 普通权限 GUI——通过本地命名管道通信。

```
┌─────────────────────────────┐        ┌────────────────────────────────────────┐
│  OpenWire.App  (WPF, 用户)  │  命名  │  OpenWire.Service  (提权)              │
│  - 全部七个界面             │◄─管道─►│  - ETW 按进程采集                      │
│  - 实时图表 + 列表          │  JSON  │  - IPHLPAPI 连接表                     │
│  - 下发拦截/扫描/确认       │        │  - Windows 防火墙 (INetFwPolicy2)      │
└─────────────────────────────┘        │  - GeoIP + 反向 DNS + 局域网扫描       │
                                       │  - 警报引擎 + SQLite 历史              │
                                       └────────────────────────────────────────┘
                                                       │
                                         %ProgramData%\OpenWire\openwire.db
```

| 项目 | 说明 |
|------|------|
| `OpenWire.Core` | 共享领域模型 + 多态命名管道 IPC 契约。 |
| `OpenWire.Service` | 监控引擎。提权运行，承担全部特权操作。 |
| `OpenWire.App` | WPF 界面。普通用户权限，仅与引擎通信。 |

完整功能与 UI 规范见 [`docs/SPEC.md`](docs/SPEC.md)。

---

## 构建与运行

**要求：** Windows 10/11、[.NET SDK 9](https://dotnet.microsoft.com/download)。

```powershell
# 一键脚本：构建 → 启动提权引擎（UAC 提示）→ 启动应用
./run.ps1
```

或手动：

```powershell
dotnet build OpenWire.sln -c Release
Start-Process src/OpenWire.Service/bin/Release/net9.0-windows/OpenWire.Service.exe -Verb RunAs
Start-Process src/OpenWire.App/bin/Release/net9.0-windows/OpenWire.App.exe
```

> **未提权时**引擎仍可显示全局图表、连接列表、应用列表和局域网扫描——但无法记录每应用
> 字节数（ETW），也无法执行拦截。完整体验请以管理员身份运行引擎。

**可选 GeoIP：** 将 MaxMind `GeoLite2-Country.mmdb` 放入 `%ProgramData%\OpenWire\`
（未随附 — MaxMind 要求免费授权）。
**可选设备厂商名：** 将 Wireshark 风格的 `manuf` 文件放在 `%ProgramData%\OpenWire\manuf.txt`。

---

## 诚实的局限

- **首包拦截。** 拦截委托给 Windows 防火墙（基于规则，而非内联数据包回调），全新应用在
  拦截规则生效前可能发出少量数据包。真正的首包拦截需要签名的 WFP callout 驱动——无驱动
  构建有意不做此项。
- **无云端信誉评分** — 没有后端，这是设计决定。
- **摄像头/麦克风监控**有意省略（Windows 无可靠 API）。

## 路线图

- 基于同一 IPC 契约的远程 / 多机监控
- 可选 WFP callout 驱动，实现精确计量 + 首包拦截
- 带宽限制 / 按应用限速

---

## 地区说明

台湾、香港、澳门都是中国的一部分。如果您不认同，请停止使用本软件。

## 许可证

[MIT](LICENSE)。
