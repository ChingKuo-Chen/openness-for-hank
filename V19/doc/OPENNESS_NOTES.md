# Openness 手冊閱讀心得（V21 實戰）

最後更新：2026-09-17。對照本機 PDF [`reference/TIAPortalOpenness-enUS.pdf`](../reference/TIAPortalOpenness-enUS.pdf) 與 V21 WinCC Basic HMI 實測。

目的：之後 Agent **先讀這份再翻 PDF**，少走已證實的死路。

章節：§1 Classic vs Unified · §2 Tag 紙本 vs 實測 · §3 API 能做什麼 · §4 Connection · §5 V21 落差 · §6 工作順序 · §7 速查 · §8 再挖 PDF · §9 stall · §10 Absolute · §11 Recipe · **§12 Softkey／圖形清單／Discrete**。

---

## 1. 先分兩套 HMI，不要混 API

| | Classic（Basic / Comfort / Advanced） | WinCC Unified |
|--|----------------------------------------|---------------|
| 軟體物件 | `Siemens.Engineering.Hmi` → **`HmiTarget`** | `Siemens.Engineering.HmiUnified` → **`HmiSoftware`** |
| DLL | `Siemens.Engineering.WinCC.dll` | Unified 相關組件 |
| Tag 物件 | `Siemens.Engineering.Hmi.Tag.Tag` | `HmiTag` |
| 直接寫 PLC 綁定 | **文件沒有** `tag.PlcTag = ...` | 文件明寫 **`PlcTag` R/W** |
| 紙本綁定路徑 | SimaticML：`Connection` + **`ControllerTag`** | 屬性或 WinCCML（YAML） |

**心得：** 網路上／別的對話若丟 `hmiTag.PlcTag = "PlcTag_1"`，先確認裝置是不是 Unified。KTP Basic 是 Classic（`HmiTarget`），那招無效。

---

## 2. Classic HMI Tag：紙本寫什麼 vs V21 Basic 實測

### 紙本（Siemens）

integrated connection 的 external HMI tag：

1. Export 時「只存 HMI↔PLC **連結**，不存 PLC 資料」  
2. Import 前 PLC、PLC Tag、integrated connection 都要存在  
3. XML 用 `Connection` + `ControllerTag`（`TargetID="@OpenLink"`）  
4. `Tags.Import(..., Override)` 後「連結會再啟用」

### 本機 V21 Basic（`HmiTarget`）

| 步驟 | 結果 |
|------|------|
| HW integrated 連線 | 可 `valid=True`，但 soft `Connections.Count` 常為 0 |
| GUI 手動綁 PLC 後再 Openness Export | 有 `Connection`，**沒有 `ControllerTag`**，`LogicalAddress` 常空 |
| XML 加上 `ControllerTag` 再 Import | **失敗**：`set_ControllerTag is not supported by type '...Hmi.Tag.Tag'` |
| 列舉 Tag 名稱／Export 型別週期 | **可以** |

**心得：**

1. 紙本「Export 只存連結」在 Basic 上變成「Export **根本不寫出** `ControllerTag`」——Openness 讀不到 GUI 綁定。  
2. 紙本「Import 會重建連結」在 Basic 上撞到 **runtime 不支援 set ControllerTag**——不是我們 XML 少抄一行那麼簡單。  
3. Comfort／舊版可能仍可行；**不要用 PDF 直接宣稱 Basic V21 可自動綁**。摘要：[OPENNESS-BASIC-HMI-TAG-HANDOFF.md](../host/Practice/io-merge/hmi-rewire/tf-live/OPENNESS-BASIC-HMI-TAG-HANDOFF.md)（含 IO／Override 注意）。

### 過時建議（本 repo 曾寫過）

「手動建範本 → 改 `ControllerTag` → Import」對 **部分** Classic 仍是文件路徑；對 **V21 Basic 本專案** 已失敗。之後若再試，必須先有**新的完整可 Import XML** 或 Siemens 確認 Basic 支援，不要重跑同一條。

---

## 3. Classic Openness 對 Tag「能做什麼」（紙本 API）

文件在 `HmiTarget` 下明確示範的多半是：

- 建／刪 Tag 資料夾、Tag 表  
- **Enumerate** tags  
- **Delete** 單一 tag  
- Tag table / 單一 tag 的 **Export／Import**

**沒有**像 Unified 那樣的「屬性表：PlcTag R/W」。

**心得：** 讀名稱、改畫面綁到「已有的 HMI Tag 名」、刪孤兒 Tag——Openness 好用。  
「自動把 HMI Tag **符號**指到 PLC 路徑」（`ControllerTag`）——Basic V21 **寫不進**。  
**已證實可行：** Absolute 位址 Import（§10），不是 Internal、也不是 ControllerTag。

---

## 4. 連線（Connection）

紙本：

- soft `HmiTarget.Connections`：可列舉、可 Export／Import **非** integrated 連線  
- **Export of integrated connections is not supported**

實測：Devices & Networks 上 integrated 連線 valid，但 `hmi.Connections.Count` 常為 0；`Connections.Create` 可能炸。HW 層用 `HardwareSync` 看得到 `HmiConnection`。

**心得：** 查連線先掃 **HW DeviceItem**，不要只信 soft Composition。

---

## 5. V21 與舊 PDF 的落差（必記）

| 舊範例常見寫法 | V21 實務 |
|----------------|----------|
| 單一 `Siemens.Engineering.dll` | `Base` + `Step7` + `WinCC`（+ Extension）分拆 |
| `TagTable`（PLC） | **`PlcTagTable`** |
| `Tags.Create(name)` 再設位址 | **一次** `Create(name, type, address)` |
| 舊 `HW.dll` / `SW.dll` | **不要引用** |

防火牆 AllowList：每次重編譯 exe 都會再問——`Build.ps1` 應更新 AllowList；人要按 **Yes to all**。

---

## 6. 工作順序建議（之後會比較快）

1. 裝置是 Basic／Comfort 還是 Unified？→ 決定 API 命名空間。  
2. 只要 **列舉／改畫面 Tag 名／編譯 HMI** → Openness。  
3. 要 **寫 PLC 綁定**：  
   - Unified → `PlcTag` 屬性（先對照 Info System）。  
   - Classic Basic → **不要**走 `ControllerTag`；走 **Absolute**（§10）或 GUI／Excel。  
4. Import Override 後 **不要再用舊的 `Tag` 物件**（會 disposed）——用名稱重新 `Find`。  
5. 任何「文件說可以」≠「這台可以」——以 compile／**re-export 仍有 Connection+%DB** 為準。

---

## 7. 速查：Basic HMI 寫綁定時要記

- 裝置是 Basic／Comfort → `HmiTarget`；Unified → 另一套 API  
- 連線名以 **HW Devices & Networks** 為準，不是 soft `Connections` 清單  
- 找 PLC 用**軟體名**（硬體裝置名常不同）  
- 寫綁定 → Absolute（§10）；Recipe 定義 → GUI（§11）；Softkey／Discrete → §12

---

## 8. 還想從 PDF 挖什麼時

優先搜 PDF／Info System 關鍵字：

- `Special considerations` + `HMI tags`  
- `ControllerTag` / `integrated connection`  
- `HmiSoftware` + `PlcTag`（Unified）  
- `EnumerateTagsInTagtable`  
- `Export of integrated connections`

---

## 9. 長跑 Openness：自己檢查有沒有卡住（必做）

**不要等使用者問「你有在跑嗎」。** 丟出 `TiaOpennessCheck.exe` 後，Agent 要自己監看；卡住就殺、診斷、重跑。

### 怎麼判斷卡住

約 **30～60 秒** 看一次（編譯／匯出可放寬到 2～3 分鐘仍無新 log 再當卡住）：

| 訊號 | 卡住 |
|------|------|
| `TiaOpennessCheck` 還在，但 **CPU 幾乎不涨** | 多半在等 UI／COM 鎖／掃大型專案 |
| host 輸出 log / `TiaOpennessCheck.log` **長度與 LastWriteTime 不動** | 沒有新進度 |
| 最後一行停在「目標專案：…」或「連線成功」之後很久 | 常見：開場 **全專案掃 PLC**（HMI-only 指令不該走這條） |
| Portal 跳出 Openness 防火牆／對話框沒人按 | 進程活著、log 停住 |

### 卡住後怎麼做

1. `Stop-Process -Name TiaOpennessCheck -Force`（必要時再查 PID）  
2. 看最後一條 log、Portal 是否還開著目標 `.ap21`  
3. 對症：防火牆 **Yes to all**；HMI-only 略過全掃 PLC；不要同時 Attach 多個卡住的 host  
4. 重跑並繼續自己監看，**不要丟給使用者「請你開 TIA／請你看有沒有在跑」**

### 相關

- 踩雷摘要 → [OPENNESS_PITFALLS.md](OPENNESS_PITFALLS.md)（長跑停滯）  
- HMI-only 指令應略過開場 `FindAllPlcSoftwares`，避免卡死

---

## 10. Basic HMI 連 PLC：Absolute 成功路徑

**目標：** Tag 在 Portal 裡真的連著 PLC（有 Connection + 位址），不是空的 Internal。

### 做法（已跑通）

1. HW 連線已有；不必 soft `Connections.Create`。  
2. 需要 Absolute 的 DB／相關塊設 **標準存取**，必要時先編譯 PLC。  
3. 建位址表：PLC Tag → `%I/%Q/%M`；DB 成員 → `%DBn.DBD/DBW/DBX…`（Number + Offset／版面）。  
4. **整張** Tag 表 Export → 改每顆：  
   - `AddressAccessMode` = `Absolute`  
   - `LogicalAddress` = `%DBn.…` / `%M…` 這類  
   - `Connection` = HW 連線名  
   - **不要**加 `ControllerTag`  
5. `TagTables.Import(Override)` → Save → Compile HMI → **再 Export 驗證**。

### 驗證（Import 後再 Export）

PASS 時應看到：

| 欄位 | 期望 |
|------|------|
| AddressAccessMode | Absolute |
| LogicalAddress | `%DBn.DBD/DBW/DBX…` 或 `%M…` 等 |
| Connection | 實際 HW 連線名 |
| ControllerTag | **無** |

純 HMI 內部 Tag（例畫面頁碼）可維持 Symbolic／Internal。

### 不要做

| 做法 | 為什麼 |
|------|--------|
| `ControllerTag` Import | runtime `set_ControllerTag is not supported` |
| 整表 Internal（拿掉 Connection） | 編譯假過、**沒連 PLC** |
| Unified `PlcTag =` | Classic Basic 是 `HmiTarget` |

### Host／實作備註

- 找 PLC 用**軟體名**（硬體裝置名可能不同）  
- HMI-only 指令略過開場全掃 PLC，避免卡死  
- 精簡注意卡（IO／Override／Absolute 選路）：[OPENNESS-BASIC-HMI-TAG-HANDOFF.md](../host/Practice/io-merge/hmi-rewire/tf-live/OPENNESS-BASIC-HMI-TAG-HANDOFF.md)

### 與 Recipe 無關

畫面／Tag Absolute 綁定與 Recipe 定義是分開的問題 → §11。

---

## 11. Recipes（Basic）：官方邊界＋通用結論

### 官方（Openness／WinCC）

| 來源 | 結論 |
|------|------|
| Openness「可 Import/Export 的 HMI 物件」表 | Screens、Tags、Tag tables、Connections、Cycles、Text/Graphic lists、Scripts…**沒有 Recipes** |
| Openness Object list | **Recipe view = No**（Basic／Comfort／Mobile／RT Advanced 皆 No） |
| WinCC「Importing recipes…／Export recipe data」 | GUI／Runtime 對 **配方資料錄** 做 **CSV／TXT** Export／Import；不是 Openness XML 刪「Recipe 定義」 |
| `HmiTarget` 公開屬性（V21 WinCC.dll） | 無 `RecipeFolder`；`GetCompositionInfos` 亦無 Recipe* |

雲端入口（對照用）：

- [Basic principles of importing/exporting](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/export/import/overview/basic-principles-of-importing/exporting)  
- [Object list](https://docs.tia.siemens.cloud/r/en-us/v21/tia-portal-openness-api-for-automation-of-engineering-workflows/tia-portal-openness-api/tia-portal-openness-object/object-list)  
- [Importing recipes into the configuration…](https://docs.tia.siemens.cloud/r/en-us/v21/working-with-recipes-basic-panels-panels-comfort-panels-rt-advanced/configuring-recipes-basic-panels-panels-comfort-panels-rt-advanced/creating-and-editing-recipes-basic-panels-panels-comfort-panels-rt-advanced/importing-recipes-into-the-configuration-and-exporting-them-basic-panels-panels-comfort-panels-rt-advanced)

本機 PDF [`reference/TIAPortalOpenness-enUS.pdf`](../reference/TIAPortalOpenness-enUS.pdf) 亦寫 Recipe view 不在可匯出畫面物件之列。

### 通用心得

1. GUI Recipes 編輯器 Export → **資料錄 CSV**，不能當 Openness 刪 Recipe 定義的替代。  
2. 要拿掉整筆 Recipe 定義 → **Portal GUI 刪除**。  
3. 不要為消編譯錯去建舊前綴／Internal 短名廢物；PLC 顯示綁定走 Absolute（§10）。
4. 編譯若只剩 Recipe 元素 Tag／Recipe 畫面按鈕（`RecipeView*`），與 Discrete／Softkey **分開算**。TIA 能開、Recipe 還要用 → **不要刪 Recipe 定義**。Openness 也刪不掉（本節）。

---

## 12. 畫面 Softkey／圖形清單／Discrete（解法，不要重試）

搬畫面後編譯錯的修法。不要把機台畫面名當硬體設計依據。已證實走不通的路見下表，不要再試。

### Softkey（Global screen）

編譯：「`Softkey_Fn` Press key，referenced object does not exist」＝ **ActivateScreen 指到本機沒有的畫面**。

| 事實 | 作法 |
|------|------|
| Softkey 在 **`ScreenGlobalElements`**，不在各畫面 XML | 逐畫面 Export 改 Softkey **沒用**，還容易卡住 |
| SimaticML 節點是 `Hmi.Screen.SoftKey` | 用 `LocalName == "SoftKey"` 會掃不到；要比對 `EndsWith(".SoftKey")` 或看 `ObjectName` |
| 物件有 `Export`，**沒有** `Import` | 匯入走 **`HmiTarget.ImportScreenGlobalElements(FileInfo, ImportOptions)`**（WinCC.xml 有寫）。`composition.Import`／`hmi.Import` 都不存在 |
| 缺畫面（參考機有、這台沒有） | **拿掉**該鍵的 `ActivateScreen` 條目，不要留死連結；有的畫面才改名（例 18B→20B） |

改完 Import Override → Save → 再 Compile 該台 HMI。驗證：Global Softkey 錯應消失。

### 圖形清單 vs 畫面名

- **畫面／ActivateScreen** 可以改名（例 `P05_18B_*` → `P05_20B_*`）。  
- **GraphicList／PictureList 實體名**若還叫 18B，不要跟畫面一起改字串，否則「graphics list invalid」。  
- **不要批次 `GraphicList.Export`**：Basic 常炸，甚至 **dispose `HmiTarget`**，後面全部掛掉。需要時只改範本裡的 `PictureList` 連結名。

### Discrete alarms（沒有 Openness）

WinCC Basic **沒有**可用的 `DiscreteAlarms` composition。一定走編輯器 xlsx。點法 → [HOW-TO-DISCRETE.md](HOW-TO-DISCRETE.md)。

編譯：「The bit number of the trigger tag for the discrete alarm with ID *n* is invalid」常見兩類：

1. **偶數 byte 的 IEC `.0` 要用 HMI `.8`**（已寫在 HOW-TO）。同一 Word（`AErr_LW{k}`）bit 仍是 0..15。  
2. **同一 Trigger tag 上兩個 alarm 搶同一個 bit**（匯入／複製後 8–14 重複一輪）。同一 Word 每個 bit 只能一筆。重複的那批改到空著的 0–6（或換下一顆 Word），不要再 `^=8` 亂翻。

xlsx 改完再 Import：

- 用 **正斜線** zip 路徑（`xl/worksheets/sheet1.xml`）。`CreateFromDirectory` 在 Windows 常寫成 `xl\...`，TIA 報 `Import failed (0032:000011)`，**log 是空表**。  
- 改 bit 用字串替換 `<v>舊</v>`，不要整份 `XmlDocument.Save` 打掉 `x:` 命名空間。  
- 成功：`Import finished: N discrete alarms`。然後 `--save`；要對 compile 的 bit 錯才 Compile **該台** HMI。

Agent **不要猜 Import 圖示座標**、不要對整棵 TIA 做 UIA Descendants。編輯器開好再匯；點不到就請人點「紙＋向內箭頭」。

### 與 Recipe 分開修

非 Recipe（Softkey、圖形清單、Discrete bit）可以先清。Recipe 元素 Tag／P09 `RecipeView*` 按鈕屬 §11，使用者沒說動就不要刪 Recipe。
