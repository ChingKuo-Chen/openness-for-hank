# LAD／SCL 練習庫（全員共用）

本目錄把**已在練習專案驗證過**的寫法放進 Git，供任何一台開發機／任何 Agent 使用。  
**不依賴**某人本機的舊 `TiaOpennessCheck` 路徑。

實績摘要：對照 23019 的 FB/FC 名稱，**101 LAD + 41 SCL**（外加後續補充）曾在 `PRACTICE_1215` 與 `Main` 編譯 **0 錯誤**。語言跟來源走，不要互轉。

---

## 目錄

| 路徑 | 內容 |
|------|------|
| [Practice/*.lad.xml](../Practice/) | LAD 規格（給 `LadWriter`，**不要**手改 generated） |
| [Practice/Scl/*.scl](../Practice/Scl/) | SCL 源碼 |
| [Practice/SclDump/](../Practice/SclDump/) | 參考機 SCL 邏輯摘要（註解用） |
| [Practice/examples/](../Practice/examples/) | 最短入門範本 |

規範：

| 檔 | 何時讀 |
|----|--------|
| [rules/tia-write-program.md](../rules/tia-write-program.md) | 寫到 0 錯的流程與坑 |
| [rules/tia-lad-spec.md](../rules/tia-lad-spec.md) | 規格骨架、埠名、禁止項 |
| [rules/scl-style.md](../rules/scl-style.md) | SCL／DB 風格 |

歷史交接（路徑已過期，只當背景）：[templates/handoffs/HANDOFF-LAD-Practice.md](../templates/handoffs/HANDOFF-LAD-Practice.md)。**以本檔與 `rules/` 為準。**

---

## 本機還要自備什麼

Git **不放**日常 `.ap21`。各開發者自備練習／機台副本，例如：

| 項目 | 說明 |
|------|------|
| 練習用 `.ap21` | 自建或從團隊共用盤複製；CPU 建議含一台無 TO 的 1215 級可驗證可攜塊 |
| Openness host | 本 repo：`cd host; .\Build.ps1` → `host\TiaOpennessCheck.exe` |
| 參考程式 | 寫機台時問 2～3 個參考路徑（可不給，改用 Practice）；用到的摘錄放機台 `reference/` |

---

## 建議驗證指令（路徑請改成你的）

```powershell
cd <本 repo>
.\host\Build.ps1   # 若尚無 exe

$proj = '<你的練習.ap21絕對路徑>'
$plc  = 'PRACTICE_1215'   # 或你專案裡的 PLC 名
$spec = (Resolve-Path .\Practice\examples\Blink.lad.xml).Path

.\host\TiaOpennessCheck.exe --project:$proj --plc:$plc --write-lad:$spec
$gen = [IO.Path]::ChangeExtension($spec, '.generated.xml')  # 以工具實際輸出為準
.\host\TiaOpennessCheck.exe --project:$proj --plc:$plc --import-block:$gen
.\host\TiaOpennessCheck.exe --project:$proj --plc:$plc --compile
.\host\TiaOpennessCheck.exe --project:$proj --save
```

批次：`.\tools\lad\Import-Practice-Batch.ps1`（會讀本 repo 的 `Practice\`）。

---

## Agent 行為

寫 LAD／SCL 時：

1. 讀 `tia-write-program.md` + `tia-lad-spec.md`
2. 需要範例時打開 `Practice/examples/` 或同名 `Practice/<Block>.lad.xml`／`Practice/Scl/<Block>.scl`
3. 用本 repo `host\` 編譯／匯入；回報編譯錯誤數
4. **不要**假設舊包 `…\TiaOpennessCheck\Practice` 存在

機台寫作仍遵守：問 2～3 個參考路徑（可不給，改用 Practice）、摘錄放機台 `reference/`、只改工作副本。練習庫是**寫法與可攜塊**，不是某一台出貨專案。
