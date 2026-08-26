# Cursor Rules 修剪 SOP — 新專案 onboarding

> **人類怎麼觸發** → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md)  
> **觸發**：使用者說 **「加入專案 `<name>`」**，在 [add_project_onboarding.md](../templates/add_project_onboarding.md) 複製 template 之後執行修剪。  
> **目標**：`.cursor/rules/` 內**全部為已解析路徑**；專案內產出 **`doc/REFERENCE_PATHS.md`**。  
> **禁止**：rule 內留 `<CPU>`、`<DLL>`、`<!-- CUSTOMIZE -->` 等需 Agent 再推導的占位符。

---

## 為什麼要修剪

| 層 | 檔案 | 角色 |
|----|------|------|
| 總地圖 | `openness-standard/reference/tia_v21.md` | V21 預設路徑（探索用） |
| 專案捷徑 | `<project>/.cursor/rules/project-local.mdc` | 日常開專案／編譯時**直接有路徑** |
| 交付物 | `<project>/doc/REFERENCE_PATHS.md` | onboarding 產出的**已解析路徑表** |

Template 只是空白表單；**填滿**後不得留占位符。

---

## 與 `openness-standard/rules/` 的分工

| 複製進專案（`.mdc`） | 留在 central（`rules/*.md`） |
|----------------------|------------------------------|
| 本專案路徑、CPU、DLL、dev 閉環 | Openness 禁則、排版、release、分支 |
| **路由與摘要** | **完整必守規範** |

- **禁止**把 `rules/*.md` 全文貼進 `.mdc`。
- **禁止**在 `.mdc` 寫與 `rules/` 矛盾的規則。

---

## Phase 1 — 解析身份（只讀，不猜）

| # | 讀什麼 | 得到什麼 |
|---|--------|----------|
| 1 | 本機 `Siemens.Engineering.dll`（見 [tia_v21.md](../reference/tia_v21.md) 查詢命令） | **實際** DLL 完整路徑 |
| 2 | 專案內 `.ap21` / 文件記載的專案檔 | 專案檔路徑 |
| 3 | 使用者／README 的 CPU、station | MLFB、station 名 |
| 4 | 找不到就停，問使用者；**不要**填 V16 路徑 |

---

## Phase 2 — 複製 template

```text
mkdir <name>/.cursor/rules
複製 templates/cursor-rules/*.mdc → <name>/.cursor/rules/
project-local.mdc.example → project-local.mdc
```

**兩層規範**：專案 `.mdc` = 路由；法典 = [rules/README.md](../rules/README.md)。

---

## Phase 3–5 — 修剪

將 `project-local.mdc`、`openness-routing.mdc`、`skip-vendor-tree.mdc` 的 glob／路徑改為 **本機真實字串**。

---

## Phase 6 — REFERENCE_PATHS.md

用 [REFERENCE_PATHS.md.example](../templates/REFERENCE_PATHS.md.example) 填滿。

---

## Phase 7 — 驗收

- DLL 路徑在檔案系統存在（或已註明「待使用者安裝」且 ONBOARDING ⬜）
- 無 `<CPU>`、`<!-- CUSTOMIZE -->`

---

## Phase 8 — 請使用者檢查

請使用者打開 **`.cursor/rules/project-local.mdc`** 確認定位、V21、CPU、station、DLL。有誤再改。確認後 ONBOARDING「人工確認」改 ✅。
