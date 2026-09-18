# Cursor Rules 修剪 SOP — 新專案 onboarding

> **人類** → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md)  
> **觸發**：`加入專案 <name>`  
> **目標**：`.cursor/rules/` 內為已解析路徑；必留 `find-standard.mdc`；產出 `doc/REFERENCE_PATHS.md`。  
> **禁止**：rule 內留占位符；刪掉 `find-standard.mdc`。

---

## 為什麼要修剪

| 層 | 檔案 | 角色 |
|----|------|------|
| 總地圖 | `openness-standard/reference/tia_v21.md` | V21 預設路徑 |
| 找 standard | `<project>/.cursor/rules/find-standard.mdc` | 沒加 workspace 也能找 |
| 專案捷徑 | `<project>/.cursor/rules/project-local.mdc` | `.ap21`、CPU、**standard 根路徑** |
| 交付物 | `<project>/doc/REFERENCE_PATHS.md` | 已解析路徑表 |

---

## 與 `openness-standard/rules/` 的分工

| 複製進專案（`.mdc`） | 留在 central（`rules/*.md`） |
|----------------------|------------------------------|
| 找 standard、本專案路徑、CPU、DLL | Openness 禁則、排版、release、分支 |

- **禁止**把 `rules/*.md` 全文貼進 `.mdc`。
- **不要刪** `find-standard.mdc`、`tia-hmi-autoload.mdc`、`tia-tools-autoload.mdc`。

---

## Phase 1 — 解析身份（只讀，不猜）

| # | 讀什麼 | 得到什麼 |
|---|--------|----------|
| 1 | [find-standard.mdc](../templates/cursor-rules/find-standard.mdc) 搜尋順序 | **standard 根目錄**（必要時 clone 到同層） |
| 2 | 本機 V21 `PublicAPI\V21\net48\` | DLL 目錄 |
| 3 | 專案內記載的 `.ap21` | 專案檔路徑 |
| 4 | 使用者／README 的 CPU、station | MLFB、station 名 |
| 5 | 找不到就停，問使用者；**不要**填 V16 路徑 |

---

## Phase 2 — 複製 template

```text
mkdir <name>/.cursor/rules
複製 templates/cursor-rules/*.mdc → <name>/.cursor/rules/
project-local.mdc.example → project-local.mdc
templates/AGENTS.md.example → <name>/AGENTS.md（若尚無）
```

**不要**複製 `templates/host/` 當日常步驟。

---

## Phase 3–5 — 修剪

將 `project-local.mdc` 的 standard 根路徑、`.ap21`、DLL、PLC 改為真實字串。

---

## Phase 6 — REFERENCE_PATHS.md 與參考摘錄

用 [REFERENCE_PATHS.md.example](../templates/REFERENCE_PATHS.md.example) 填滿（含 standard 路徑）。

問使用者 **2～3 個**參考 TIA 路徑（可空）。複製 [reference/README.md.example](../templates/reference/README.md.example) → `<name>/reference/README.md`。沒給路徑就註明改用本樹 `Practice/`。之後實際用到的塊／硬體／IO 才放進 `reference/`。

寫 `<name>/<name>.code-workspace`（[project.code-workspace.example](../templates/project.code-workspace.example)）。**不要**複製 `.cursor/skills/` 進機台。全隊 skill 只在 `openness-standard/.cursor/skills/`。

---

## Phase 7 — 驗收

- standard 目錄存在且有 `host/Build.ps1`
- DLL 路徑存在（或 ONBOARDING ⬜ 待裝 TIA）
- 無 `<CPU>`、`<!-- CUSTOMIZE -->`
- `find-standard.mdc` 仍在

---

## Phase 8 — 請使用者檢查

打開 **`.cursor/rules/project-local.mdc`**。請使用者 **Open Workspace** `<name>.code-workspace`（含 standard），全隊 skill 才會掛上。
