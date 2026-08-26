# Agent 工作指引 — TIA Openness

本 repo 是 **對 Agent 下指令的入口**。使用者用自然語言描述需求即可，Agent 依本文件、`doc/` 手冊與 `rules/` 規範執行。

**本 org 目標是 TIA Portal Openness，不是 MCU。** 禁止把 edge 韌體流程（flash、isptool、pyocd、TRM）套用到本 workspace。

## 設計因果（必讀）

**先有 TIA 專案（硬體組態 + 程式塊），才有 Openness 腳本，才有出貨 archive。**  
C# host 用來驅動 Portal，**不可用既有 C# 反推 PLC 設計**。

→ [plans/PRINCIPLES.md](plans/PRINCIPLES.md)

| 規劃 / 新專案 | 自動化已存在、事後對照 |
|---------------|------------------------|
| TIA 硬體與塊 → 本機 `.ap21` → Openness 操作 | Phase B 可對照 host 是否與 Portal 專案一致 |
| **禁止** 以 C# 常數當硬體設計依據 | **禁止** 寫「以 Program.cs 為準」當 PLC 設計來源 |

---

## 設計原則（Openness — 必讀）

1. **鎖定 TIA Portal V21** — 路徑見 [reference/tia_v21.md](reference/tia_v21.md)。
2. **專案自包含** — 見 [rules/repo-dependencies.md](rules/repo-dependencies.md)。
3. **閉環** — 影響實機／Portal 專案的改動：`implement → compile → export/讀結果 → 證據`。見專案 `agent-dev-loop.mdc` 與 [rules/tia-openness-rules.md](rules/tia-openness-rules.md)。
4. **法典不 copy 進專案** — 專案 `.mdc` 只做路由。見 [rules/README.md](rules/README.md)。

---

## 使用者怎麼下指令（範例）

```
加入專案 line-a-tap
line-a-tap 進度為何
line-a-tap 寫分支對照表
line-a-tap 歸檔分支 try/db-layout
```

**加入專案（新 repo 進 workspace）** → 人類入門 [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md)；Agent 清單 [templates/add_project_onboarding.md](templates/add_project_onboarding.md)；rules 修剪 [plans/cursor_rules_trim_sop.md](plans/cursor_rules_trim_sop.md)。產物含 **`.cursor/rules/`**、**`doc/ONBOARDING.md`**、**`doc/REFERENCE_PATHS.md`**、Openness host 骨架；完工請使用者檢查 **`project-local.mdc`**。**查進度**：`<name> 進度為何` → 讀 ONBOARDING 回報 ✅/⬜。

Agent 收到 **加入專案** → 走 [templates/add_project_onboarding.md](templates/add_project_onboarding.md)。收到 **分支對照表** → 走 [doc/BRANCHES_MD.md](doc/BRANCHES_MD.md)。收到 **分支歸檔** → 走 [rules/branch-naming-sop.md](rules/branch-naming-sop.md)。

---

## 分支對照表工作流程

**觸發**：`幫我 <name> 寫分支對照表` · `更新 <name> branches.md` · `維護 <name> doc/branches.md`

| Step | 動作 |
|------|------|
| 1 | `cd <name>/`；`git fetch origin --prune`（**必做**，以 GitHub 遠端為準） |
| 2 | `git for-each-ref refs/remotes/origin` 取得各 **遠端** 分支 tip、committer date、subject（略過 `origin/HEAD`） |
| 3 | 判斷主線（`refs/remotes/origin/HEAD`）；必要時比對 `origin/main..origin/branch` / 反向 |
| 4 | 建立或更新 **`<name>/doc/branches.md`**（範本 [templates/branches.md.example.md](templates/branches.md.example.md)） |
| 5 | 文首寫 **本表最後更新**（日期 + 遠端主線 @ commit）；主表**不列**僅本地未 push 分支 |
| 6 | 回覆摘要；commit/push **僅在使用者要求時** |

通則：[doc/BRANCHES_MD.md](doc/BRANCHES_MD.md)

---

## 分支歸檔工作流程

**觸發**：`<name> 歸檔分支 <branch>` · `<name> archive merged <branch>` · `archive <branch> in <name>` · `列出 <name> 可歸檔分支`

| Step | 動作 |
|------|------|
| 1 | `cd <name>/`；`git fetch origin --prune` |
| 2 | 判斷僅本地 / 遠端存在；使用者是否指定「先不動」 |
| 3 | merged 驗證（`archive/merged/` 必須通過）；失敗則停下報告 |
| 4 | 執行歸檔：本地 `git branch -m` 或遠端 push 新名 + delete 舊名 |
| 5 | 若有 `doc/branches.md` → 更新已歸檔小表、從活躍表移除舊名 |
| 6 | 回覆摘要；commit / push **僅在使用者要求時** |

通則：[rules/branch-naming-sop.md](rules/branch-naming-sop.md)

---

## 加入專案工作流程

**觸發**：使用者已 create repo、clone、**Add Folder to Workspace**，再說 `加入專案 line-a-tap`。

| Step | 動作 |
|------|------|
| 0 | 確認 `<name>/` 在 **Cursor workspace** |
| 1 | repo 名 → 機台／產線定位；讀 [reference/tia_v21.md](reference/tia_v21.md) |
| 2 | 若無 host：複製 [templates/host/](templates/host/) 進專案（**copy，勿 include 本 repo**） |
| 3 | **Cursor rules**：複製 template → 依 [cursor_rules_trim_sop.md](plans/cursor_rules_trim_sop.md) 修剪為**已解析路徑**；產出 `doc/REFERENCE_PATHS.md` |
| 4 | 建立/更新 **`doc/ONBOARDING.md`** |
| 5 | **請使用者檢查** `.cursor/rules/project-local.mdc` |
| 6 | 使用者問 **`<name> 進度為何`** → 讀 ONBOARDING 回報 |

詳細清單：[templates/add_project_onboarding.md](templates/add_project_onboarding.md)

---

## Openness 開發閉環（摘要）

影響 Portal 專案或可下載邏輯時，預設閉環：

```text
implement → TIA Openness compile → 匯出/讀結果 → (PASS? done : fix → compile)
```

細則在專案 `.cursor/rules/agent-dev-loop.mdc` 與 [rules/tia-openness-rules.md](rules/tia-openness-rules.md)。  
使用者明說「只改檔不編譯」「docs only」時可跳過編譯。

GitHub-hosted Actions **不能**跑 TIA Portal。驗證在本機 Windows + V21。

---

## 相關入口

| 文件 | 說明 |
|------|------|
| [README.md](README.md) | 人類總覽 |
| [rules/README.md](rules/README.md) | 規範索引 |
| [doc/OPENNESS.md](doc/OPENNESS.md) | Openness 手冊 |
| [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md) | 加入專案（人類） |
