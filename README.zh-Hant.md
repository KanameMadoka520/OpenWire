<div align="center">

<img src="docs/logo.png" width="140" alt="OpenWire logo" />

# OpenWire

**開源的 Windows 網路監控 + 應用程式防火牆。**

[English](README.md) · [简体中文](README.zh-Hans.md) · **繁體中文**

</div>

OpenWire 是對 GlassWire 式桌面體驗的淨室（clean-room）開源重實作：即時捲動的頻寬圖表、
按行程歸因的流量統計、雙向應用程式防火牆、世界連線地圖、區域網路裝置掃描、帶異常偵測的
用量分析，以及網路安全警報引擎——完全免費，無帳號、無遙測、無付費牆。

> OpenWire 是獨立專案，**與 GlassWire / SecureMix LLC 無任何隸屬或背書關係，也不包含其
> 任何原始碼**。它基於公開文件與可觀察行為構建，程式碼與資源全部原創。

![流量圖](docs/screenshots/traffic-berry-day.png)

---

## 亮點

- **即時流量圖** — 平滑捲動、自動縮放的下載/上傳面積圖，時間範圍從 5 分鐘到一個月。
  在圖上橫向拖曳即可框選任意時段查看精確總量；拖動底部時間軸的圓形拖柄即可縮放，
  檢視全幀率跟手。
- **按行程歸因** — 精確到每個應用程式的收發流量，帶真實圖示、發佈者、每應用火花線與
  子行程列表。基於 **ETW** 擷取，無核心驅動。
- **應用程式防火牆** — 透過 Windows Defender 防火牆雙向允許/攔截任意應用程式，支援
  **按網路自動切換的設定檔**（Wi-Fi SSID / 閘道指紋識別）以及新應用程式的「連線詢問」
  對話方塊。
- **世界連線地圖** — 可縮放的分級設色地圖 + **3D 地球**雙檢視，點擊國家可下鑽查看主機。
- **篩選面板** — GlassWire 式側欄，按應用程式/主機/流量類型/國家地區列出用量，支援
  WAN/LAN、方向與搜尋過濾。
- **分析 + 異常偵測** — 小時/每日用量規律、流量最高應用程式、白話重點，自動偵測流量
  尖峰、重上傳應用、新國家與異常時段活動；另有 hosts 檔案與 ARP 欺騙完整性監控。
- **警報** — 新應用程式、新裝置、DNS 變更、入站 RDP、流量方案閾值、用量異常——分組、
  可篩選、可搜尋。
- **掃描器** — 盤點區域網路內每台裝置（IP、MAC、廠商、類型、首次發現、線上狀態）。
- **硬體資源** — CPU / 記憶體 / 磁碟 / GPU 即時曲線，與流量圖同款平滑捲動。
- **四套主題即時熱切換** — 簡約（扁平）、鉛筆素描（手繪）、草莓日間/夜間（痛車風），
  外加 **English / 简体中文 / 繁體中文** 三語介面，全部無需重新啟動。
- **VirusTotal 整合** — 可選，使用你自己的免費 API key 做雜湊查詢（只有 SHA-256 離開
  本機）。
- **純本地設計** — 無帳號、無雲端、無遙測，歷史資料存於本地 SQLite。

## 主題

| 鉛筆素描 | 簡約 |
|---|---|
| ![Pencil](docs/screenshots/traffic-pencil.png) | ![Minimal](docs/screenshots/traffic-minimal.png) |

| 草莓日間 | 草莓夜間 |
|---|---|
| ![Berry day](docs/screenshots/traffic-berry-day.png) | ![Berry night](docs/screenshots/traffic-berry-night.png) |

## 更多畫面

| 世界地圖 | 硬體 |
|---|---|
| ![Map](docs/screenshots/map.png) | ![Hardware](docs/screenshots/hardware.png) |

![Settings](docs/screenshots/settings.png)

---

## 架構

OpenWire 由兩個行程組成——提權背景引擎 + 一般權限 GUI——透過本地命名管道通訊。

```
┌─────────────────────────────┐        ┌────────────────────────────────────────┐
│  OpenWire.App  (WPF, 使用者)│  命名  │  OpenWire.Service  (提權)              │
│  - 全部七個畫面             │◄─管道─►│  - ETW 按行程擷取                      │
│  - 即時圖表 + 列表          │  JSON  │  - IPHLPAPI 連線表                     │
│  - 下發攔截/掃描/確認       │        │  - Windows 防火牆 (INetFwPolicy2)      │
└─────────────────────────────┘        │  - GeoIP + 反向 DNS + 區網掃描         │
                                       │  - 警報引擎 + SQLite 歷史              │
                                       └────────────────────────────────────────┘
                                                       │
                                         %ProgramData%\OpenWire\openwire.db
```

| 專案 | 說明 |
|------|------|
| `OpenWire.Core` | 共享領域模型 + 多型命名管道 IPC 契約。 |
| `OpenWire.Service` | 監控引擎。提權執行，承擔全部特權操作。 |
| `OpenWire.App` | WPF 介面。一般使用者權限，僅與引擎通訊。 |

完整功能與 UI 規範見 [`docs/SPEC.md`](docs/SPEC.md)。

---

## 建置與執行

**需求：** Windows 10/11、[.NET SDK 9](https://dotnet.microsoft.com/download)。

```powershell
# 一鍵指令碼：建置 → 啟動提權引擎（UAC 提示）→ 啟動應用程式
./run.ps1
```

或手動：

```powershell
dotnet build OpenWire.sln -c Release
Start-Process src/OpenWire.Service/bin/Release/net9.0-windows/OpenWire.Service.exe -Verb RunAs
Start-Process src/OpenWire.App/bin/Release/net9.0-windows/OpenWire.App.exe
```

> **未提權時**引擎仍可顯示全域圖表、連線列表、應用程式列表和區網掃描——但無法記錄每應用
> 位元組數（ETW），也無法執行攔截。完整體驗請以系統管理員身分執行引擎。

**可選 GeoIP：** 將 MaxMind `GeoLite2-Country.mmdb` 放入 `%ProgramData%\OpenWire\`
（未隨附 — MaxMind 要求免費授權）。
**可選裝置廠商名：** 將 Wireshark 風格的 `manuf` 檔案放在 `%ProgramData%\OpenWire\manuf.txt`。

---

## 誠實的侷限

- **首包攔截。** 攔截委託給 Windows 防火牆（基於規則，而非內聯封包回呼），全新應用程式
  在攔截規則生效前可能發出少量封包。真正的首包攔截需要簽名的 WFP callout 驅動——無驅動
  建置有意不做此項。
- **無雲端信譽評分** — 沒有後端，這是設計決定。
- **攝影機/麥克風監控**有意省略（Windows 無可靠 API）。

## 路線圖

- 基於同一 IPC 契約的遠端 / 多機監控
- 可選 WFP callout 驅動，實現精確計量 + 首包攔截
- 頻寬限制 / 按應用程式限速

---

## 地區說明

台灣、香港、澳門都是中國的一部分。如果您不認同，請停止使用本軟體。

## 授權

[MIT](LICENSE)。
