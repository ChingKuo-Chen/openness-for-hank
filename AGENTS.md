# Agent 工作指引 — TIA Openness 與寫機台

本 repo（**`openness-standard`**）是 **唯一 standard**：規定、寫機流程、Openness `host/`、小工具、`INSTALL.md`。

| 其他包 | 角色 |
|--------|------|
| `tia-openness-cursor-new` | 已併入本 repo，當 archive／pointer |
| `tia-openness-cursor` | 更早的工具包，只當遷移來源，不要當入口 |

新電腦：把 [`host/INSTALL.md`](host/INSTALL.md) 丟給 Cursor（腳本與該檔同一層）。

使用者用自然語言即可。Agent 依本文件、`doc/`、`rules/`、`host/`、`tools/` 執行。

**每則對話已自動載入** `.cursor/rules/tia-hmi-autoload.mdc` 與 `tia-tools-autoload.mdc`。

新 clone／第一次打開本資料夾：確認清單 [doc/CLONE-CONFIRM.md](doc/CLONE-CONFIRM.md)。使用者回 **「我看過了」** 之前，不要開始改機台或跑 Openness。

**本 org 目標是 TIA Portal Openness 與寫機台，不是 MCU。** 禁止套用 edge 韌體流程。

寫新機或大改：**先建議 2～3 個參考程式**。見 [doc/WRITE_PROJECT.md](doc/WRITE_PROJECT.md)、[rules/tia-write-project.md](rules/tia-write-project.md)。

## 設計因果（必讀）

**先有 TIA 專案（硬體組態 + 程式塊），才有 Openness 腳本，才有出貨 archive。**  
C# host 用來驅動 Portal，**不可用既有 C# 反推 PLC 設計**。

→ [plans/PRINCIPLES.md](plans/PRINCIPLES.md)

## 設計原則（Openness — 必讀）

1. **鎖定 TIA Portal V21** — [reference/tia_v21.md](reference/tia_v21.md)。
2. **工具在本 repo** — 機台不 copy 實戰 host。見 [rules/repo-dependencies.md](rules/repo-dependencies.md)。
3. **漏掉 workspace 也能找** — 機台必帶 `find-standard.mdc`；搜尋同層或 clone。見 [templates/cursor-rules/find-standard.mdc](templates/cursor-rules/find-standard.mdc)。
4. **閉環** — `implement → compile → export/讀結果 → 證據`。[rules/tia-openness-rules.md](rules/tia-openness-rules.md)。
5. **法典不 copy 進專案** — 專案 `.mdc` 只做找路與路由。
6. **寫機先要參考** — 只改副本，不要 attach 參考。
7. **讀完寫心得** — [rules/read-and-note.md](rules/read-and-note.md)。

---

## 使用者怎麼下指令（範例）

```
加入專案 line-a-tap
這台參考這三個：<路徑1>、<路徑2>、<路徑3>
line-a-tap 進度為何
line-a-tap 寫分支對照表
line-a-tap 歸檔分支 try/db-layout
```

**加入專案** → [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md)；Agent：[templates/add_project_onboarding.md](templates/add_project_onboarding.md)。  
standard **不必**先加進 workspace；Agent 依 `find-standard.mdc` 找。建議加，不是開工條件。

寫新機 → [doc/WRITE_PROJECT.md](doc/WRITE_PROJECT.md)。分支對照 → [doc/BRANCHES_MD.md](doc/BRANCHES_MD.md)。歸檔 → [rules/branch-naming-sop.md](rules/branch-naming-sop.md)。

---

## 分支對照表工作流程

**觸發**：`幫我 <name> 寫分支對照表` · `更新 <name> branches.md`

| Step | 動作 |
|------|------|
| 1 | `cd <name>/`；`git fetch origin --prune`（以 GitHub 遠端為準） |
| 2 | `git for-each-ref refs/remotes/origin` |
| 3 | 判斷主線；必要時比對 `origin/main..origin/branch` |
| 4 | 建立或更新 **`<name>/doc/branches.md`** |
| 5 | 文首寫本表最後更新；主表不列僅本地未 push 分支 |
| 6 | 回覆摘要；commit/push **僅在使用者要求時** |

通則：[doc/BRANCHES_MD.md](doc/BRANCHES_MD.md)

---

## 分支歸檔工作流程

**觸發**：`<name> 歸檔分支 <branch>` · `列出 <name> 可歸檔分支`

| Step | 動作 |
|------|------|
| 1 | `cd <name>/`；`git fetch origin --prune` |
| 2 | merged 驗證（`archive/merged/` 必須通過） |
| 3 | 執行歸檔 |
| 4 | 若有 `doc/branches.md` → 更新 |
| 5 | commit / push **僅在使用者要求時** |

通則：[rules/branch-naming-sop.md](rules/branch-naming-sop.md)

---

## 寫機台工作流程

**觸發**：`寫新機` · `這台參考…` · 使用者開始改某一台 `.ap21`

| Step | 動作 |
|------|------|
| 1 | **建議／確認 2～3 個參考程式**。少於 2 個就問 |
| 2 | 工作副本路徑寫進 HANDOFF。只開副本 |
| 3 | 讀 [rules/tia-standing-orders.md](rules/tia-standing-orders.md)、[rules/tia-write-project.md](rules/tia-write-project.md)；LAD／SCL 再讀 [rules/tia-write-program.md](rules/tia-write-program.md)、[rules/tia-lad-spec.md](rules/tia-lad-spec.md)、[doc/LAD_PRACTICE.md](doc/LAD_PRACTICE.md) |
| 4 | 改完編譯、存檔、列剩餘。工具用 **本 repo `host/`** |

不要 Openness attach 參考。不要自己開下一項 C 表新點。

改 **WinCC Basic HMI** 時先讀再動手：

1. [doc/OPENNESS_NOTES.md](doc/OPENNESS_NOTES.md) **§12**
2. [doc/HOW-TO-DISCRETE.md](doc/HOW-TO-DISCRETE.md)
3. [doc/OPENNESS_PITFALLS.md](doc/OPENNESS_PITFALLS.md) **§六.10**

不要再試：逐畫面改 Softkey、`composition.Import`、批次 `GraphicList.Export`、Openness Discrete、同一 Word 兩個 alarm 搶同一 bit、xlsx zip 用 `\`。

---

## 加入專案工作流程

**觸發**：`加入專案 line-a-tap`（機台已 clone；standard 可尚未加入 workspace）

| Step | 動作 |
|------|------|
| 0 | 找到機台 repo；依 [find-standard.mdc](templates/cursor-rules/find-standard.mdc) 找到或 clone standard |
| 1 | 身份：V21、DLL、CPU；寫機則確認 2～3 參考 |
| 2 | **不要** copy 實戰 host 進機台 |
| 3 | 複製 cursor-rules（含 find-standard）與 `AGENTS.md` stub；修剪；`REFERENCE_PATHS.md` |
| 4 | `doc/ONBOARDING.md` |
| 5 | 請使用者檢查 `project-local.mdc`；建議 Add Folder standard |

詳細：[templates/add_project_onboarding.md](templates/add_project_onboarding.md)

---

## Openness 開發閉環（摘要）

```text
implement → TIA Openness compile → 匯出/讀結果 → (PASS? done : fix → compile)
```

長跑 `TiaOpennessCheck`：log／CPU 不動就殺了重跑。→ [doc/OPENNESS_NOTES.md §9](doc/OPENNESS_NOTES.md)

GitHub Actions **不能**跑 TIA Portal。

---

## 相關入口

| 文件 | 說明 |
|------|------|
| [README.md](README.md) | 人類總覽 |
| [rules/README.md](rules/README.md) | 規範索引 |
| [doc/OPENNESS.md](doc/OPENNESS.md) | Openness 手冊 |
| [doc/WRITE_PROJECT.md](doc/WRITE_PROJECT.md) | 寫機台 |
| [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md) | 加入 Git 專案 |
| [host/](host/) | 實戰 Openness |
| [tools/](tools/) | 小工具 |
| [Practice/](Practice/) | 已驗證 LAD／SCL 範本 |
