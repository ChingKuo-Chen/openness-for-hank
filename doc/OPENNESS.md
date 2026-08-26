# TIA Openness 手冊

鎖定 **TIA Portal V21**。安裝路徑與副檔名 → [reference/tia_v21.md](../reference/tia_v21.md)。必守 → [rules/tia-openness-rules.md](../rules/tia-openness-rules.md)。

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
