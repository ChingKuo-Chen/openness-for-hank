# TIA Openness 必守規則

> 鎖定 **TIA Portal V21**。路徑細節 → [reference/tia_v21.md](../reference/tia_v21.md) · 手冊 → [doc/OPENNESS.md](../doc/OPENNESS.md)。  
> 專案路由 → `templates/cursor-rules/openness-routing.mdc`、`project-local.mdc`。

---

## P0

1. **不要提交 Siemens DLL。** 只以本機 PublicAPI 路徑 reference；見 [repo-dependencies.md](repo-dependencies.md)。
2. **不要提交二進位 TIA 專案當日常 diff 來源。** Git 以可讀產物為主（SCL 源、DB 表、host C#、文件）。`.ap21` / `.zap21` 僅在 `release-sop` 或專案明文允許時入庫。
3. **同一時間一個工程實例。** Openness 對已開啟的專案是互斥的。Agent 不得並行開第二個 Portal 去搶同一 `.ap21`。
4. **編譯是閉環的一部分。** 改會影響 PLC 行為的塊／硬體／host 後，必須走 compile → 讀結果；除非使用者明說 docs-only。
5. **失敗要留證據。** 編譯錯誤、Openness exception、找不到 DLL，寫進 `plan/*_progress.md` 或當輪回報，禁止只說「應該可以」。

---

## 開啟與權限

- 以 **同一 Windows 使用者** 跑 Cursor / host 與 TIA Portal。
- 該使用者須在 Portal 的 Openness 授權名單內（TIA 設定 → Openness）。未授權時停止並請使用者開權限，不要重試盲連。
- 優先 attach 既有 V21 程序；沒有再啟動。啟動超時、授權對話框 → **HUMAN**，不要假成功。

---

## 編譯與匯出

| 動作 | 規則 |
|------|------|
| Compile | 對目標 PLC／專案編譯；記錄 warning/error 條數 |
| 判定 PASS | 0 error。Warning 須對照專案 `project-local.mdc` 是否允許 |
| Export | 匯出 SCL/DB/硬體清單等到 repo 約定目錄（見專案 REFERENCE_PATHS） |
| Download | **預設不做**。僅當使用者明說下載到 PLC／PLCSIM |
| Archive | 出貨走 [release-sop.md](release-sop.md)，產 `.zap21` |

---

## 禁止

- 把 MCU 燒錄、isptool、pyocd、TRM 流程套用到 Openness 專案
- 在 ISR／即時語意下討論 SCL（PLC 掃描週期 ≠ MCU ISR）
- 修改 `Siemens.Engineering*` 或 Program Files 內檔案
- 假設 GitHub Actions 能編譯 TIA
- 用 V15/V16 DLL 操作 V21 專案

---

## 回報格式（開發閉環）

```text
[Cycle N] compile: OK/FAIL | export: OK/SKIP | judge: PASS/FAIL
Evidence: <錯誤摘要 / 編譯 log 路徑>
Next: <fix 摘要 或 PASS 結案>
```
