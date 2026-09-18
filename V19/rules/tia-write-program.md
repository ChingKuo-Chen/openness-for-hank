# LAD／SCL 寫到編譯 0 錯

本檔 = 在練習專案實測過的寫法（23019 對照：**142** 個 FB/FC = **101 LAD + 41 SCL**，兩台 PLC 皆 0 錯）。機台案同一條路。

規格骨架 → [tia-lad-spec.md](tia-lad-spec.md)。  
範本庫 → [../Practice/](../Practice/) · 說明 → [../doc/LAD_PRACTICE.md](../doc/LAD_PRACTICE.md)。  
SCL 註解風格 → [scl-style.md](scl-style.md)。

語言跟來源走。不要互轉。不要從參考機 XML 複製再改名。畫面看起來對不算；匯出對過、編譯 0 錯才算。`--compile` 後再 `--save`。

可以對照參考程式的**行為**；埠名仍不准猜，以匯出／`LadWriter` 指令表為準。

---

## LAD（LadWriter）

```
寫 Practice/<Block>.lad.xml   （或機台 repo 內同等規格）
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --plc:<PLC> --write-lad:<絕對路徑.spec.xml>
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --plc:<PLC> --import-block:<絕對路徑.generated.xml>
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --plc:<PLC> --compile
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --save
```

`--import-block:` **必須絕對路徑**（相對路徑會被錯接）。

| 做對 | 踩過的坑 |
|------|----------|
| 比較 `pre`→`out` | 寫成 `en` |
| 運算／Move／Calc `en`→`eno` | |
| TON／TOF／TP：`IN`→`Q`，`time="T#2s"` | `const="T#2s"` |
| `Inc`/`Dec` 埠 `operand` | |
| `Parallel` 至少兩支 | 一支 → Card=1 Import 失敗 |
| `Fork`：同一來源一條 Wire、多個目標 | 同一來源兩條 Wire |
| `Calc` 常數當 IN | 方程式裡寫字面常數 |
| `MIN`/`MAX` 用小寫 `card` | 大寫 `Card`；`Sub`/`Div` 不要 Card |
| 新指令：指令表加一列，Version 從匯出抄 | 手寫 FlgNet／猜埠名 |
| `CTRL_HSC` 不要 `DisabledENO` | 一 FB 多顆 bare 實例會 Create 失敗 |
| Modbus CMD 9 欄；`to_read`／`to_write` 分 struct | 同 struct |

匯入後再匯出，對 parts／wires，確認 TIA 沒把結構降級。一次少匯。資源警告 `0024:000004`：關區塊分頁、`tools/gui/Dismiss-TiaDialog.ps1`、必要時重開 TIA。

---

## SCL（External Source）

```
寫 Practice/Scl/<Block>.scl
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --plc:<PLC> --import-scl:<絕對路徑.scl>
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --plc:<PLC> --compile
host\TiaOpennessCheck.exe --project:<絕對路徑.ap21> --save
```

`--import-scl` **會先刪同名區塊**再 Generate（LAD→SCL 換語言必需，否則衝突）。批次：`--import-scl-dir:` 或 `tools/lad/Import-Practice-Batch.ps1`（一次少許，避免 TIA 資源耗盡）。生在根目錄的 FB／FC／DB 立刻搬進編號夾；OB 留外層跟 `Main` 同一層。

| 做對 | 踩過的坑 |
|------|----------|
| `CASE 0:` 與 `CASE 1:` 兩行 | `CASE 0, 1:` |
| 參數名 `in_val` | 叫 `IN`／`Out`（保留字） |
| `LReal + REAL_TO_LREAL(Real)` | `LReal + Real` |
| `WORD_TO_DINT` 再 `DINT_TO_REAL` | `WORD_TO_REAL * 65536` |
| 輸出給初值，reset 再清零 | 未初始化警告（如 `LRealValCnt`） |
| 標頭用 `//`，介面用 `VAR_INPUT`／`VAR_IN_OUT` | |
| 呼叫端腳位維持原樣 | 為了改語言改掉 InOut |

曾必須維持 SCL（勿再寫成 LAD）的例子：`Enc_Accu`、`Enc_Diff`、`Get_Finish_Length`、`Interpolation_Lookup_adv`（腳位見 `Practice/Scl/`）。

DB 用 External Source 或 XML Import，不要當程式語言選。
