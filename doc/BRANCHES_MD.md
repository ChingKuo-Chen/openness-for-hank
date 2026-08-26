# branches.md 通則 — `<project>/doc/branches.md`

各 TIA 專案 repo 可維護一份 **Git 分支對照表**，方便辨識主線、實驗線與 merge 狀態。

**資料來源：** 以 **GitHub 遠端**（`origin`，`git fetch` 後的 `refs/remotes/origin/*`）為準，**不是**本地 `refs/heads`。本地獨有、未 push 的分支不列入主表。

**分支命名與歸檔：** `try/`、`service/`、`archive/` 等 → [rules/branch-naming-sop.md](../rules/branch-naming-sop.md)。

---

## 觸發

使用者說（`<name>` = repo 名）：

- **幫我 `<name>` 寫分支對照表**
- **更新 `<name>` branches.md**
- **維護 `<name>` doc/branches.md**

Agent 在 **該專案** `<workspace>/<name>/doc/branches.md` **新增或更新**（不寫入 openness-standard，除非改通則本身）。

Ask / Agent 模式皆可；更新時 **必須** `git fetch origin --prune` 後查 `origin/*` tip，勿憑記憶填表，勿只讀本地 `refs/heads`。

---

## 檔案位置

```
<project>/doc/branches.md
```

範本：[templates/branches.md.example.md](../templates/branches.md.example.md)

---

## 必備內容

1. **文首「本表最後更新」** — 日期 + 主線分支名 + 短 commit
2. **分支一覽表** — 欄位：分支 | 最新 commit | 最後更新 | 用途 / 狀態
   - 資料來自 **`origin/<branch>`**
   - 主線標 ⭐（以 `origin/HEAD` 為準）
3. **（建議）已歸檔** — `archive/*`
4. **（建議）主線最近改動摘要**
5. **維護指令** — 見範本

---

## Agent 工作流程

| Step | 動作 |
|------|------|
| 1 | `cd <project>`；`git fetch origin --prune` |
| 2 | `git for-each-ref refs/remotes/origin` |
| 3 | 判斷主線：`git symbolic-ref refs/remotes/origin/HEAD` |
| 4 | 建立或更新 `doc/branches.md` |
| 5 | 回覆摘要；**僅在使用者要求時** commit / push |

若 `git fetch` 失敗，**不要**用過期本地資料填表。

---

## 前綴與表格分表

| 前綴 | 語意 | 列入哪張表 |
|------|------|------------|
| `try/<主題>` | 短期試做 | 活躍主表 |
| `service/<客戶>` | 售後維護 | 活躍主表 |
| `archive/merged/<原名>` | 已合進主線 | 已歸檔小表 |
| `archive/abandoned/<原名>` | 未 merge、放棄 | 已歸檔小表 |
| `archive/superseded/<原名>` | 被取代 | 已歸檔小表 |
