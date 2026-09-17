# 寫專案要注意（參考／IO／Import／交接）

最後更新：2026-09-17。

全隊入口：[rules/tia-write-project.md](../../../../../rules/tia-write-project.md)、[rules/tia-standing-orders.md](../../../../../rules/tia-standing-orders.md)。  
Openness／Basic HMI 死路與 Absolute → [doc/OPENNESS_NOTES.md](../../../../../doc/OPENNESS_NOTES.md) §2／§10；Recipe → §11；Softkey／Discrete → §12。  
機台交接範本 → [templates/HANDOFF.md.example](../../../../../templates/HANDOFF.md.example)。

---

## 參考程式補缺（寫新機／大改）

- 先要 **2～3 個**參考；少於 2 個就問，不要只用一份。  
- 參考**只讀**；只改工作副本。**不要 Openness attach 參考**（會寫進參考）。  
- 其中一個適合當底 → `robocopy` 整包到桌面再改名 `.ap21`，不要用 Openness「另存」參考。  
- **缺的站／功能**（給線、捲取、預扭…）從對應參考抄寫法／塊／畫面；缺的 PLC 用 MasterCopy 或無介面開源專案拷進副本，再接網路。  
- 讀完每個參考 → 寫 `doc/notes/REF-*.md`（大綱＋心得），不要只留在聊天。  
- 機台 HANDOFF 表格要列齊這 2～3 個完整路徑。

---

## 對話快滿 → 給下一輪的摘要

跨對話 Agent **不會記住**聊天內容。交接靠**檔案**，不是靠「記得上次說什麼」。

- **對話快滿／要換新對話前**：先更新該機 `handoffs/HANDOFF-*.md`（已做／還沒做／踩坑／下一輪第一步）。  
- 那份 HANDOFF＝**給下一輪任何 Cursor 用的摘要**；路徑寫完整。  
- 做完一項要列剩餘，對齊 HANDOFF「還沒做」→ [list-remaining.md](../../../../../rules/list-remaining.md)。  
- 讀過的參考／手冊心得落 `doc/notes/` 或 `OPENNESS_NOTES`，下一輪先翻檔再整份重讀。

---

## IO 對照（對 C 表）

- Hardware Tag **名不改**（程式綁名）；C 表說明可寫 Comment。  
- 比對取最高分：**Tag 名**、**TIA 註解**、**舊 IO 表說明**（參考程式）。≥80% → **名留程式底、址改 C 表**。  
- 文字對不到：舊名先搬到 C 表新址，不要留舊址撞名。  
- 同址功能對不上：名留舊的，列 PROBLEMS，先討論再改邏輯。  
- C 表沒寫、程式還在用的舊 Tag **不要刪**。  
- OP1／OP2 同一功能兩個位址都留；不要自己開下一項 C 表新點。  
- 位址衝突先看該 CPU **Overview of addresses**。

細節與 Excel 正規化 → `tia-write-project.md`「IO Excel」。

---

## 覆蓋／取代（Import Override）

- `Import(..., ImportOptions.Override)`＝**用這份 XML 整份蓋掉同名物件**，不是合併差分。  
- 匯入前：先 **Export 備份**；改的是**工作副本**，不要 attach／改參考專案。  
- Tag 表：對**整張表**做 Export → 改 → `TagTables.Import(Override)`；不要以為只改一顆就只動一顆。  
- 塊／SCL：`--import-scl` 會先刪同名再 Generate（換語言必需）；匯入覆蓋既有塊前要有同意或計劃明文（`scl-style.md`）。  
- Override 後舊的 `Tag`／`TagTable`／塊物件常 **disposed** → 用**名稱重新 Find**，不要繼續用匯入前的 reference。  
- XML 裡 `ID="18B"` 這類是 hex，**不要**整份字串亂取代。

---

## Basic HMI ↔ PLC（選路，細節在 NOTES）

| 做法 | 結果 |
|------|------|
| `ControllerTag` Import | Basic V21 **失敗** |
| 整表 Internal（拿掉 Connection） | 編譯假過、**沒連 PLC** |
| Absolute + `%DB…/%M…` + Connection，無 ControllerTag | **可行**；Import 後再 Export 驗證 |

驗證以 re-export 仍有 Connection + 位址為準。Recipe 定義 Openness 刪不掉 → GUI（NOTES §11）。

---

## 畫面 Softkey／Discrete（寫入時）

解法 → [OPENNESS_NOTES.md §12](../../../../../doc/OPENNESS_NOTES.md)。

- Global Softkey 在 `ScreenGlobalElements`。匯入用 **`HmiTarget.ImportScreenGlobalElements`**，不是 `composition.Import`。XML 節點是 `Hmi.Screen.SoftKey`。  
- 缺畫面：拿掉 `ActivateScreen`，不要留死連結。畫面名與 GraphicList 名分開改；**不要批次 Export GraphicLists**（會 dispose HmiTarget）。  
- Discrete：Basic 無 Openness。xlsx Import（[HOW-TO-DISCRETE.md](../../../../../doc/HOW-TO-DISCRETE.md)）。同一 Word 一個 bit 只能一筆 alarm；改 xlsx 的 zip 路徑要用 `/`。  
- 編譯錯先拆：Softkey／圖形清單／alarm bit 與 Recipe 元素／Recipe 畫面按鈕分開修。TIA 還能編 Recipe → 不要刪定義。
