# 加入專案 — Agent onboarding 清單

> **人類入門** → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md)。  
> 使用者已：create repo → clone 到與 `openness-standard` **同層**（建議）→ 對 Agent 說 **「加入專案 `<repo>`」**。  
> 不必先 Add Folder `openness-standard`。Agent **只改該機台 repo**。

---

## 觸發句

| 使用者說 | Agent 理解 |
|----------|------------|
| **加入專案 `<name>`** | 對 `<name>/` 放找路規則、ONBOARDING、project-local |
| **`<name>` 進度為何** | 讀 `<name>/doc/ONBOARDING.md` 回報 ✅/⬜ |
| 同義 | `新增一個專案 <name>` · `<name> onboarding 進度` |

---

## Agent 執行順序

### Step 0 — 找到機台 repo 與 standard

- 機台路徑：workspace 內 `<name>/`，或與 standard 同層的該資料夾。機台不在 workspace → **建議** Add Folder（機台）；仍可用磁碟絕對路徑改檔。
- **standard** 依 [find-standard.mdc](cursor-rules/find-standard.mdc) 搜尋：workspace → `../openness-standard` → `pioneerm-automation\openness-standard` → `gh repo clone pioneerm-automation/openness-standard` 到機台**同層**。
- 找到就讀、就用 `host/`。**不要**因為 standard 沒進 workspace 而停工。
- 建立 `<name>.code-workspace`（含 `../openness-standard`），請使用者開 **Workspace** 而不是只開機台資料夾。全隊 skill 在 standard [`.cursor/skills/`](../../.cursor/skills/README.md)，不要 copy 進機台。

### Step 1 — 身份

- TIA：**V21**（[reference/tia_v21.md](../reference/tia_v21.md)）
- 查本機 Openness DLL（`PublicAPI\V21\net48\`）
- 記錄 CPU / station（未知則在 project-local 寫「待填」並在 ONBOARDING ⬜，**不要編造 MLFB**）
- **問 2～3 個參考 TIA 路徑**（可空）。有給：列入 HANDOFF／`doc/REFERENCE_PATHS.md`。沒給：註明改用本樹 `Practice/`。建立 `<name>/reference/README.md`（範本 [reference/README.md.example](reference/README.md.example)）。見 [doc/WRITE_PROJECT.md](../doc/WRITE_PROJECT.md)
- 之後實際從參考抄到的塊／硬體／IO **只把用到的部分**放進 `<name>/reference/`，不要整包 `.ap19`／`.ap21`，不要 Openness attach 參考

### Step 2 — 不要 copy 實戰 host

日常工具用 **standard** 的 `host/`。  
**不要**把 [templates/host/](host/) copy 進機台當預設步驟。  
（出貨、對方沒有 standard 時才考慮最小骨架，另說。）

### Step 3 — Cursor rules（薄層）

依 [cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md)：

1. 複製 `templates/cursor-rules/` → `<name>/.cursor/rules/`（**必含** `find-standard.mdc`，不要刪）
2. `templates/AGENTS.md.example` → `<name>/AGENTS.md`（若尚無）
3. 修剪為已解析路徑；`project-local.mdc` 寫上 **standard 根目錄**、`.ap21`、PLC
4. 建立 `doc/REFERENCE_PATHS.md`
5. 複製 `templates/reference/README.md.example` → `<name>/reference/README.md`
6. 寫 `<name>/<name>.code-workspace`（範本 [project.code-workspace.example](project.code-workspace.example)：機台 `.` + `../openness-standard`）。**不要**把 `.cursor/skills/` copy 進機台。
7. 請使用者 **Open Workspace** 該檔（全隊 skill 才會自動掛上），並檢查 `project-local.mdc`

### Step 4 — ONBOARDING.md

一律建立或更新（範本：[ONBOARDING.md.example](ONBOARDING.md.example)）。

### Step 5 — 回報

摘要表格 + 請使用者 **Open Workspace** `<name>.code-workspace`，並打開 `project-local.mdc`。全隊 skill 在 standard `.cursor/skills/`，不要 copy 進機台。

**不要**在第一波實作完整產 DB／建硬體的 Openness 功能碼。
