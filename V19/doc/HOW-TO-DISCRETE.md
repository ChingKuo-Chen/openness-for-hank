# 以後自己點 Discrete alarms（不要猜座標）

WinCC Basic 沒有 Openness `DiscreteAlarms`。一定走編輯器 xlsx。  
同一 Word 搶 bit、xlsx zip 路徑 → [OPENNESS_NOTES.md §12](OPENNESS_NOTES.md)。

**Trigger bit：** 這套 HMI 很特別。偶數 byte 的 `M200.0`／`M282.0` 要選 **`.8`**，不是 `.0`。奇數 byte（`M201.0`／`M283.0`）才是 `.0`。IEC 公式不要用。

**同一 Word 一個 bit 只能一筆 alarm。** 編譯「bit number … invalid」若一串 ID 共用 `AErr_LW{k}` 且 bit 8–14 出現兩輪，是重複不是編碼。把後一輪改到空著的 0–6（或下一顆 Word）。

## 打開編輯器

1. 左條是 **Visualization**，不是 PLC programming。
2. 樹：`1.PLC&HMI` → `1.Strander` → `25017_Main_HMI` 或 `25017_OP2_HMI`。
3. `HMI alarms` **不會展開**，是葉子。**雙擊 HMI alarms**，不要雙擊 Device configuration / Connections / Recipes。
4. 右邊編輯器分頁才是 **Discrete alarms**（Discrete / Analog / System events）。它不是樹節點。

## Export / Import

標題「Discrete alarms」**左邊**兩顆：

- 紙 + 箭頭出 = Export
- 紙 + 箭頭入 = Import

專案樹收起來時，這兩顆會跑到視窗最左邊。**不要沿用舊螢幕座標。**

匯入：

1. 點 Import。
2. 必要時 `{ENTER}` 才出 `Import HMI alarms`。
3. 路徑貼完整檔名，例如 `C:\Users\Hank\Documents\Automation\HMIAlarms-pretwist.xlsx`。
4. 按 **Import**（左邊那顆，右邊是 Cancel）。
5. 狀態列應出現 `import finished: N discrete alarms`。空 log + `Import failed (0032:000011)` 多半是 xlsx zip 用了 `xl\...` 反斜線，或 XML 命名空間被重寫壞。重包用 `/`。
6. 同一份再匯 **OP2**（若這台有第二塊 Basic）。
7. `--save`。要對 compile 的 trigger-bit 錯才 Compile **該台** HMI；不要為 Discrete 掃整專案。

改 xlsx：只改 `<v>bit</v>` 字串。不要 `XmlDocument.Save` 整表（會丟 `x:` prefix，TIA 拒收）。

## 不要

- TIA 可以置頂來點；點完立刻拿掉，不要一直蓋住 Cursor。使用者要看 Cursor 做了哪些事。
- 不要對整棵 TIA 做 UIA Descendants。
- 不要猜 Import 圖示座標。編輯器開著仍點不到 → 請人點「紙＋向內箭頭」。
