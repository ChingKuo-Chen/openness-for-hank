# tools — 小工具

依用途分子目錄。Openness exe：`../host/TiaOpennessCheck.exe`（先 `cd host; .\Build.ps1`）。

| 資料夾 | 內容 |
|--------|------|
| [lad/](lad/) | LAD／SCL：匯入、讀塊、批次（讀本 repo [`Practice/`](../Practice/)） |
| [gui/](gui/) | 點 TIA 畫面、截圖、關對話框 |
| [io/](io/) | C 表／IO Excel 比對（Python；機台資料路徑另設） |

```powershell
$env:TIA_PRACTICE_PROJECT = '<你的練習.ap21絕對路徑>'
.\tools\lad\Practice.ps1 -Block Blink -SkipCompare
.\tools\gui\Dismiss-TiaDialog.ps1
```

LAD 寫法與練習庫 → [doc/LAD_PRACTICE.md](../doc/LAD_PRACTICE.md)。
