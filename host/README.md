# host — TIA Openness 主程式

從舊包 `tia-openness-cursor` 根目錄遷入。在本目錄編譯與執行。

**新電腦：把 [INSTALL.md](INSTALL.md) 丟給 Cursor。** 本目錄同時有 `Build.ps1`、`GrantAllowListAccess.ps1`。

## 建置

需 **系統管理員** 跑一次（寫 Openness AllowList），之後一般權限也可編譯（若 HKLM 寫入失敗會警告）。

```powershell
cd host
.\Build.ps1
```

產物：`TiaOpennessCheck.exe`（**不要** commit exe；本機建置即可）。

AllowList 手動補救：`.\GrantAllowListAccess.ps1`

## 內容

| 檔 | 用途（摘要） |
|----|----------------|
| `Program.cs` | CLI 入口、attach／專案／編譯／匯入匯出 |
| `StationSync.cs` | 站台／HMI 同步（體積大） |
| `HardwareSync.cs` | 硬體／IO |
| `Hmi24BEvents.cs` | HMI 事件相關 |
| `LadWriter.cs` | LAD XML 產生 |
| 其餘 `*.cs` | 複製／修復／科技物件／練習註解 |

踩雷與 V21 DLL → [doc/OPENNESS_PITFALLS.md](../doc/OPENNESS_PITFALLS.md)、[doc/OPENNESS.md](../doc/OPENNESS.md)。

## 與 `templates/host/` 的差別

| | 本目錄 `host/` | `templates/host/` |
|--|----------------|-------------------|
| 用途 | **日常實戰** Openness 工具 | 出貨／對方沒有 standard 時的最小骨架 |
| 依賴 | 機台 **跑這裡的 exe**，不要 include | 不要當每人一份實戰 host |
