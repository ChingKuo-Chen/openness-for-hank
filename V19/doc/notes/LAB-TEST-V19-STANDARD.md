# V19 Standard 實驗場結論

**最後更新**：2026-09-18  
測試專案：`C:\Users\David\Desktop\Test V19 Standard\Test V19 Standard.ap19`  
host：`openness-standard/V19/host/`（不要用 `V21/host`）  
不要拿 24147WH 當實驗場。

Portal 閃退後用**同一條** Portal V19 exe + 同一份 `.ap19` 重開。不要空開再手動找檔。

**不要** Import 只有 `<Name>`、沒有 `<Driver>` 的 HMI 連線 XML（V19 會 `NonRecoverableException`，Portal 可能沒了）。Driver 用 `SMART_S7_1200_OMS`。

硬塞 SoftKey 進空的 `ScreenGlobalElements`（`GlobalAssignment=true`）同樣會 NonRecoverable、Portal disposed。不要再試。

---

## PLC／寫機：與 V21 對等 PASS

```text
[Cycle 2] compile: OK | export: OK | judge: PASS
Evidence: PRACTICE_1215 錯誤 0／警告 0
```

| 功能 | 結果 |
|------|------|
| attach／`--project:.ap19`／開 Portal V19 | PASS |
| `--add-plc` 寫訂貨號（整段加引號） | PASS（1215C、1212C、1214C、KTP `6AV2 123-2GB03`） |
| `--add-language:zh-CN` | PASS |
| DB、PLC Tag、LAD、SCL、編譯、匯出塊／Tag、dump 硬體、存檔 | PASS |
| Practice 全庫 | PASS 144/145 |

host 為 V19 改過：`.ap19`、`Engineering version="V19"`、空專案 `TagTables.Create`、件號引號。

## 24147 影子（只在實驗場加硬體，不寫回 24147）

件號：PH/TP `6ES7 212-1AE40-0XB0/V4.5`，Main `6ES7 214-1AF40-0XB0/V4.6`，HMI `6AV2 123-2GB03-0AX0/16.0.0.0`。

| 功能 | 結果 |
|------|------|
| 加四顆 PLC + KTP | PASS |
| HMI 連線（含 Driver） | PASS `HMI_Connection_1` |
| HMI Tag Absolute（`%DB1.DBD…` + Connection，無 ControllerTag） | PASS |
| PLC `--compile-all` | PASS 錯誤 0 |

```text
[Cycle 4] compile: OK | export: SKIP | judge: PASS（Practice，略過 OP_Merge）
Evidence: PRACTICE_1215 錯誤 0；警告 SMMA_Event_ST OB91 cycle < 2 ms
```

## Openness 做不到（不要再試）

| 功能 | 結果 |
|------|------|
| `--catalog:` / `HardwareCatalog.Find` | 卡住。加 PLC 改 `--type:"OrderNumber:…"` |
| 匯入循環中斷 OB（`OP_Merge`） | 「OB system parameters must be informative」 |
| 設 HMI start screen | 屬性／ScreenGlobalElements／ScreenOverview 都不支援。人在 TIA Runtime settings 選 |
| 畫面物件 Layer／ScreenItems | 這塊 KTP 不支援（空白匯出也沒 Layer） |
| Tag 綁 Cycle（`set_Cycle`） | 不支援。Cycles 清單讀得到，寫不進 Tag。人在 Tag 表選 |
| 空 Global 硬建 SoftKey | NonRecoverable，Portal 死 |
| Recipe／Discrete Openness API | 沒有（與 V21 相同） |
| Safety F 當 LAD 寫入 | 不要。list／export 可以 |

## 能做、有限制

| 功能 | 結果 |
|------|------|
| `ImportScreenGlobalElements` roundtrip | PASS（空 Global） |
| Softkey 改已有 ActivateScreen | 空實驗場沒有材料；不要硬建 |
| Discrete xlsx | API 沒有；要人開 HMI alarms 編輯器才測得了 Import |
| `--compile-hmi`（無人手） | FAIL：無 start screen + Real1–10 無 acquisition cycle |

人在 TIA 設 start screen 與 Tag cycle 之後，才能再看 HMI 編譯是否只剩那兩類以外的錯。
