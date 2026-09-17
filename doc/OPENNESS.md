# TIA Openness 手冊

鎖定 **TIA Portal V21**。安裝路徑與副檔名 → [reference/tia_v21.md](../reference/tia_v21.md)。必守 → [rules/tia-openness-rules.md](../rules/tia-openness-rules.md)。

**西門子官方 PDF（原文，非本隊手冊）** → [reference/TIAPortalOpenness-enUS.pdf](../reference/TIAPortalOpenness-enUS.pdf)（Siemens [109477163](https://support.industry.siemens.com/cs/document/109477163)）。  
**讀手冊心得（Classic vs Unified、HMI Tag；Basic Absolute 見 §10）** → [OPENNESS_NOTES.md](OPENNESS_NOTES.md)。  
**踩雷筆記（已證實走不通）** → [OPENNESS_PITFALLS.md](OPENNESS_PITFALLS.md)。  
**Basic Softkey／圖形清單／Discrete 解法** → [OPENNESS_NOTES.md §12](OPENNESS_NOTES.md)、[OPENNESS_PITFALLS.md §六.10](OPENNESS_PITFALLS.md)、[HOW-TO-DISCRETE.md](HOW-TO-DISCRETE.md)。  
**Basic HMI↔PLC 實測包** → [doc/notes/OPENNESS-BASIC-HMI-TAG-HANDOFF.md](notes/OPENNESS-BASIC-HMI-TAG-HANDOFF.md)。  
**實戰 host** → [host/](../host/)（`Build.ps1`）。Basic HMI 連 PLC：Absolute → [OPENNESS_NOTES.md §10](OPENNESS_NOTES.md)。

分工（見 [README 目錄](../README.md)）：本隊手冊／心得／踩雷只寫在 **`doc/`**；`reference/` 只放 V21 **路徑表**與西門子 **官方 PDF 原文**，不放心得正文。

---

## 本機需要什麼

| 項目 | 說明 |
|------|------|
| Windows | Openness 只在 Windows |
| TIA Portal V21 | 含 STEP 7；Openness 元件已安裝 |
| 授權 | Portal 設定中允許目前 Windows 使用者使用 Openness |
| .NET | Host 以 .NET Framework 或專案 `host/*.csproj` 為準（範本見 [templates/host/](../templates/host/)） |

Cursor 與 host **必須與 Portal 同一使用者**。

---

## 典型流程

1. 開啟或 attach TIA Portal V21
2. 開啟專案（`.ap21`）或還原 `.zap21`
3. 依 host 指令編譯目標 PLC
4. 讀編譯訊息；0 error 才算 PASS
5. 需要時匯出 SCL／塊清單到 Git 目錄
6. 下載到 PLC／PLCSIM **僅在使用者要求時**

---

## Agent 驗證

GitHub Actions **不能**跑 Portal。驗證 = 本機閉環，回報格式見 [tia-openness-rules.md](../rules/tia-openness-rules.md)。

若 DLL 路徑與 `project-local.mdc` 不符：先請使用者確認 V21 安裝，再改 HintPath，不要改去用 V16 DLL。

---

## 檔案什麼該進 Git

| 進 Git | 通常不進 Git |
|--------|----------------|
| `host/` C# | `Siemens.Engineering*.dll` |
| 匯出的 SCL／文件 | 日常工作中的未壓縮 `.ap21`（除非專案明文） |
| `doc/`、rules、VERSION | 大型 `.zap21`（出貨可放 `release/` 且 gitignore，或依 `project-local`） |
