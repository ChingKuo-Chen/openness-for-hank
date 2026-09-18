# 讀參考／手冊 → 寫心得大綱（必守）

跨對話 Agent **不會記住**讀過的程式或 PDF。要累積，必須落成 repo 檔案。

## 何時寫

| 觸發 | 產物 |
|------|------|
| 讀完／對照完 **參考程式**（`.ap21` 匯出塊、Practice LAD、畫面…） | `doc/notes/REF-*.md`：**大綱** + **心得** |
| 讀完／對照完 **官方手冊／PDF／Siemens docs** | 更新既有心得（如 [doc/OPENNESS_NOTES.md](../doc/OPENNESS_NOTES.md)）或新開 `doc/notes/MANUAL-*.md` |
| 實測閉環成功／證實死路 | 補進心得；必要時留 XML／log 證據路徑 |

讀完同一輪用到的 **2～3 個參考**，每個至少一則 `REF-*.md`（可短，不可只放在聊天裡）。

## 失敗時先讀什麼（順序）

1. **本 repo 心得／大綱**（`doc/OPENNESS_NOTES.md`、`doc/OPENNESS_PITFALLS.md`、`doc/notes/`、相關 HANDOFF）  
2. **官方手冊／PDF**（[reference/TIAPortalOpenness-enUS.pdf](../reference/TIAPortalOpenness-enUS.pdf)、Siemens docs）對應章節  
3. 仍不夠 → **再整份重讀**參考程式或手冊（不要一失敗就從頭掃完整 `.ap21`／整本 PDF）

## `REF-*.md` 最低欄位

1. **來源**（路徑／塊名／匯出檔）  
2. **大綱**（結構、關鍵網路／介面、和本機任務有關的點）  
3. **心得**（可抄什麼、不要抄什麼、與本專案差異）  
4. **何時整份重讀**（什麼情況下心得不夠、要回去看原文）

索引：[doc/notes/README.md](../doc/notes/README.md)。

## 與寫機流程的關係

[tia-write-project.md](tia-write-project.md) 問 2～3 個參考路徑（沒給則 Practice）；**讀完就要寫 notes**，用到的摘錄放機台 `reference/`，不要只在 HANDOFF 列路徑。  
HANDOFF 可鏈到對應 `doc/notes/REF-*.md`。
