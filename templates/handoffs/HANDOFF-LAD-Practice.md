# LAD / SCL 練習 — 交接摘要（2026-08-26）

> **已遷入標準 repo。** 全員請以  
> [`doc/LAD_PRACTICE.md`](../../doc/LAD_PRACTICE.md)、[`rules/tia-write-program.md`](../../rules/tia-write-program.md)、[`rules/tia-lad-spec.md`](../../rules/tia-lad-spec.md)、[`Practice/`](../../Practice/)  
> 為準。下文路徑（舊 `TiaOpennessCheck`、桌面 `.ap21`）僅歷史紀錄。

給後續對話用。**23019 全部 142 個 FB/FC 名稱已在 `LAD_PRACTICE` 完成：101 LAD + 41 SCL，`PRACTICE_1215` 與 `Main` 皆編譯 0 錯誤。**

語言規則跟 23019 走：**原本是 LAD 的用 LAD，原本是 SCL 的用 SCL。** 不要互相改寫。

---

## 專案與路徑

| 項目 | 路徑 |
|---|---|
| 工具倉庫 | `C:\Users\Hank\Documents\Codex\2026-08-14\new-chat-3\outputs\TiaOpennessCheck` |
| TEST 專案（硬體+軟體重建） | `C:\Users\Hank\Desktop\TEST_PROJECT_CUSOR\TEST_PROJECT_CUSOR.ap21` |
| 練習專案 | `C:\Users\Hank\Desktop\LAD_PRACTICE\LAD_PRACTICE.ap21` |
| 驗證 PLC | `PRACTICE_1215`（CPU 1215C） |
| 練習 PLC Main | `Main`（與 1215 同批 142 區塊） |
| LAD 規格 | `Practice\*.lad.xml`（**101** 份） |
| SCL 源碼 | `Practice\Scl\*.scl`（**41** 份） |
| LAD 產生器 | `LadWriter.cs` → `--write-lad:<spec.xml>` |
| LAD 讀取器 | `ReadLad.ps1` |
| LAD 驗證流水線 | `Practice.ps1 -Block <Name> -SkipCompare`（`$plc` = `PRACTICE_1215`） |
| SCL 匯入 | `--import-scl:<abs.scl>` 或 `--import-scl-dir:<資料夾>`（先刪同名區塊再產生） |
| 23019 模板 | `HmiExport\Templates\23019KP_B5_TCP_V21\` |

DB 無語言選擇。工具程式（`Program.cs`、`LadWriter.cs`、`*.ps1`）用 C# / PowerShell。

---

## 本輪完成

1. **101 LAD**：全部手寫規格、1215 驗證後灌入 `Main`，0 錯誤。
2. **41 SCL**：源碼在 `Practice\Scl\`，用 `--import-scl-dir:` 批次匯入 1215 → Main，0 錯誤。
3. **4 個原 SCL 改回 SCL**（刪掉對應 `.lad.xml`）：`Enc_Accu`, `Enc_Diff`, `Get_Finish_Length`, `Interpolation_Lookup_adv`。LAD 呼叫端腳位不變。
4. `ImportSclSource` 會先刪同名區塊（LAD→SCL 換語言必需）。
5. `LRealValCnt`：`output_cnt := 0.0` 初始值 + reset 分支清零，消除未初始化警告。

---

## 盤點

- 23019 獨特 FB/FC：**142** = LAD **101** + SCL **41**。**全部完成。**
- LAD 規格數：`101`（`Practice\*.lad.xml`）
- SCL 源碼數：`41`（`Practice\Scl\*.scl`）

練習風格：廠端全域／TO 改成腳位；inline Struct / Array 代替 UDT；不要 `FirstScan` 標籤；`PTO_Ctrl` / `SPD_PTO_Ctrl` **不加** `TO_Axis` / `MC_*`（1215 沒有工藝對象）。

### SCL 關鍵接口（LAD 呼叫端不能破）

| 區塊 | 備註 |
|---|---|
| `Enc_Diff` | `bipolar, enabled, cur_cnt` + InOut `prev_cnt, diff`；always `prev_cnt := cur_cnt` |
| `Enc_Accu` | InOut `accu : LReal`；`accu := accu + REAL_TO_LREAL(diff)` |
| `Get_Finish_Length` | 原 SCL 公式 + `ramped_ref <> 0` guard |
| `Interpolation_Lookup_adv` | 2-point lerp：`p1,v1,p2,v2,expect → out`（Tape_Plate 用這組腳位） |

---

## 驗證流程

### LAD
1. `--write-lad:<spec.xml>`
2. `--import-block:<絕對路徑.generated.xml>`（相對路徑會重複接 `HmiExport`）
3. `--compile`
4. 可選：匯出比對 parts/wires

### SCL
```
.\TiaOpennessCheck.exe --project:LAD_PRACTICE.ap21 --plc:PRACTICE_1215 --import-scl-dir:Practice\Scl
.\TiaOpennessCheck.exe --project:LAD_PRACTICE.ap21 --plc:PRACTICE_1215 --compile
```
1215 過了再對 `--plc:Main` 做同樣操作，最後 `--save`。

**不要**再跑 `Practice.ps1` 針對那 4 個已刪 `.lad.xml` 的 SCL 名（會找不到規格；若規格還在會蓋掉 SCL）。

---

## 本輪新坑

- SCL `CASE 0, 1:` 不行，要拆成 `0:` / `1:` 兩行（`SMFA_code_Pioneer_Status`）。
- SCL 參數名不要用 `IN`/`Out` 等保留字（`Range_Check` 改 `in_val`）。
- `Enc_Accu`：`LReal + Real` 要 `REAL_TO_LREAL`。
- `Comp_Real`：用 `WORD_TO_DINT` 組合再 `DINT_TO_REAL`，不要 `WORD_TO_REAL * 65536`。
- `--import-scl-dir:` 一次匯 41 個約 13 分鐘；最後才 `project.Save()` 一次。
- LAD→SCL 必須先刪同名區塊，否則 Generate 會衝突。

### LadWriter / LAD 舊坑（仍有效）

| 規則 | 說明 |
|---|---|
| 比較指令電流 | `pre` → `out` |
| 運算指令電流 | `en` → `eno` |
| `Fork` | 同一來源只能一條 Wire、多個目標 |
| `Inc`/`Dec` | 埠名 `operand` |
| `Calc` | 方程式不能寫字面常數；常數當 IN |
| `CTRL_HSC` | 不要 `DisabledENO`；一 FB 多 bare 實例會 Create 失敗 |
| `Parallel` | 不能只包一個分支（Card=1 → Import 失敗） |
| Time on Call | `time="T#2s"`，不要 `const="T#2s"` |
| Modbus CMD | 9 欄；`to_read` / `to_write` 不能同 struct |
| 匯入路徑 | `--import-block:` **必須絕對路徑** |

---

## TEST_PROJECT_CUSOR

七台 PLC 0 錯誤；TO 參數已對齊。仍 pending：**HMI 重建**、**I/O 位址比對**。與 LAD 練習分開。

---

## 建議下一輪

1. TEST 專案：HMI、I/O 位址。
2. 若要接回 23019 完整 `Interpolation_Array_adv` UDT 表，另開任務，不要破壞現有 Tape_Plate 0 錯誤。
3. 練習 OB/Main 接線（若要把 142 區塊串成可跑流程）。

舊 Openness 踩雷總集：`HANDOFF-TIA-Openness.md`。
