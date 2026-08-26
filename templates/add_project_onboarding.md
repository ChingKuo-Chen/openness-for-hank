# 加入專案 — Agent onboarding 清單

> **人類入門** → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md)。  
> 使用者已完成：create repo → clone 到與 `openness-standard` 同層 → **加入 Cursor workspace** → 說 **「加入專案 `<repo>`」**。  
> Agent **只改該 repo**。

---

## 觸發句

| 使用者說 | Agent 理解 |
|----------|------------|
| **加入專案 `<name>`** | 對 workspace 內 `<name>/` 放 rules、ONBOARDING、host 骨架 |
| **`<name>` 進度為何** | 讀 `<name>/doc/ONBOARDING.md` 回報 ✅/⬜ |
| 同義 | `新增一個專案 <name>` · `<name> onboarding 進度` |

---

## Agent 執行順序

### Step 0 — 確認 repo 在 workspace

- 路徑：`<workspace>/<name>/`
- 若不在 workspace → 請使用者先 **File → Add Folder to Workspace**

### Step 1 — 身份

- TIA：**V21**（[reference/tia_v21.md](../reference/tia_v21.md)）
- 查本機 `Siemens.Engineering.dll`
- 記錄 CPU / station（未知則在 project-local 寫「待填」並在 ONBOARDING ⬜，**不要編造 MLFB**）

### Step 2 — host 骨架

若無 `host/`：

- 複製 [templates/host/](host/) 進 `<name>/host/`
- 依 [repo-dependencies.md](../rules/repo-dependencies.md) **copy-not-include**
- `.csproj` HintPath 改為 Phase 1 的真實 DLL 路徑

### Step 3 — Cursor rules

依 [cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md)：

1. 複製 `templates/cursor-rules/` → `<name>/.cursor/rules/`
2. 修剪為已解析路徑
3. 建立 `doc/REFERENCE_PATHS.md`
4. 請使用者檢查 `project-local.mdc`

### Step 4 — ONBOARDING.md

一律建立或更新（範本：[ONBOARDING.md.example](ONBOARDING.md.example)）。

### Step 5 — 回報

摘要表格 + 請使用者打開 `project-local.mdc`。

**不要**在第一波實作完整產 DB／建硬體的 Openness 功能碼，只放骨架與路徑。
