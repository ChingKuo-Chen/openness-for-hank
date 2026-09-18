# Git 分支命名與歸檔 SOP

> **適用：** pioneerm-automation 的 TIA 專案 repo，以及 `tia-openness-cursor-new`（若有長期分支）。  
> **分支對照表：** [doc/BRANCHES_MD.md](../doc/BRANCHES_MD.md) · 範本 [templates/branches.md.example.md](../templates/branches.md.example.md)  
> **Agent 觸發：** [AGENTS.md](../AGENTS.md) §分支歸檔工作流程

本檔由 edge `firmware-standard/rules/branch-naming-sop.md` 移植；適用範圍改為 TIA 專案，**不含** MCU 韌體／`make release` 產物目錄語意。

---

## 目的

1. **主線**（`main`）維持產品預設開發線。
2. **有前綴的分支**一眼可知用途：`try/` 試做、`service/` 售後維護、`archive/` 已結案。
3. **已結案**以 `archive/<狀態>/<原名>` 歸檔，可知 merge / 放棄 / 取代。
4. 與各專案 `doc/branches.md` 搭配：主表列活躍線（含 `try/*`、`service/*`），小表列 `archive/*`。

Git **沒有遠端 rename**；改名 = **推新名 + 刪舊名**（或僅本地 `git branch -m`）。

---

## 適用範圍

| 專案類型 | 是否適用 |
|----------|----------|
| TIA 專案 repo（機台／產線） | **是** |
| `tia-openness-cursor-new`（規範 repo 本身） | 是（若有長期分支） |
| 之後的獨立 host 工具 repo | **否**（另訂；本 SOP 不涵蓋） |

---

## 分支分類總覽

| 類型 | 命名 | 是否活躍 | 語意 |
|------|------|:--------:|------|
| 主線 | `main` / `master` | ✅ | 產品預設開發線（`origin/HEAD`） |
| 短期實驗 | `try/<主題>` | ✅ | **試做**新想法；尚未決定是否納入產品 |
| 售後維護 | `service/<客戶或專案>` | ✅ | 已交貨、**持續接需求**的客戶線 |
| 已結案 | `archive/<狀態>/<原名>` | ❌ | 僅留參考；日常勿 checkout |
| 舊式短名 | `<短名>`（無前綴） | ✅ | 歷史分支；**新分支請用前綴**，舊名不強制改 |

GitHub 會將 `/` 顯示為資料夾分組；`git branch -r` 會將同前綴排在一起。

**永不動：** `main` / `master`。

**勿用為分支前綴：** `release/`（與出貨 archive 目錄 `release/*.zap21` 撞名）。

---

## 命名規範

### `try/<主題>` — 短期實驗

試做新做法、尚未決定是否 merge 進主線。**不是**「已交付的新功能」。

| 項目 | 說明 |
|------|------|
| 範例 | `try/openness-compile`、`try/db-layout` |
| 結束時 | merge 進 `main` → 刪分支或 `archive/merged/try-<主題>`；放棄 → `archive/abandoned/try-<主題>` 或刪遠端 |
| 勿用 | `feature/<主題>`（新分支）— 易誤解為「已規劃要上的功能」而非「還在試」 |

### `service/<客戶或專案>` — 售後維護線

客戶已出貨、**還會持續改**；單次交貨快照用 **tag**（`v<x.y.z>`、訂單 tag），不靠分支前綴表達「出貨」。

| 項目 | 說明 |
|------|------|
| 範例 | `service/walsin-lihwa` |
| 與 main | 長期分叉可接受；客戶需求在此改，勿在 `main` 上改客戶專屬行為 |
| 結束時 | 客戶全面升級、確定不再改 → `archive/merged/service-<客戶>` 或 `archive/superseded/...` |

### `archive/<狀態>/<原名>` — 已結案

| 前綴 | 語意 | 範例 |
|------|------|------|
| `archive/merged/<原名>` | 已合進主線，僅留參考 | `archive/merged/try-db-layout` |
| `archive/abandoned/<原名>` | 未 merge、確定放棄 | `archive/abandoned/try-v16-dll` |
| `archive/superseded/<原名>` | 被其他分支或主線另種實作取代 | `archive/superseded/legacy-export` |

歸檔時 `<原名>` 建議扁平化為 `try-主題`、`service-客戶`，避免多層 `archive/merged/try/foo`。

### 無前綴短名（舊式）

歷史分支可繼續使用。**新分支**請依上表選 `try/` 或 `service/`。

---

## 何時用哪種前綴

| 情境 | 動作 |
|------|------|
| 日常產品開發 | `main` |
| 短期試新做法、不確定要不要 | `try/<主題>` |
| 客戶已出貨、持續要改 | `service/<客戶>`；單次交貨仍用 tag |
| 實驗成功、merge 進 main、**確定不再改** | 歸檔 → `archive/merged/<原名>` 或刪遠端 |
| 實驗成功、merge 進 main、**可能還要改** | 保留原名於活躍表（勿歸檔） |
| 實驗結束、未 merge | `archive/abandoned/<原名>`，或直接刪遠端 |
| 功能已在 main 用不同方式實作 | `archive/superseded/<原名>` |
| 有 open PR 綁舊分支 | 先關 PR 或與使用者確認 |
| 僅本地、從未 push | 只 `git branch -m` |
| 使用者明說「先不動」 | **跳過**，即使已 merge |

**刪除 vs 歸檔：** 確定永遠不需要參考 → 可直接 `git push origin --delete <branch>`；想保留 commit 指標供對照 → 歸檔。

---

## 歸檔前驗證（必做）

在專案根目錄：

```bash
git fetch origin --prune
MAIN=$(git symbolic-ref refs/remotes/origin/HEAD | sed 's@^refs/remotes/origin/@@')
```

PowerShell：

```powershell
git fetch origin --prune
$MAIN = (git symbolic-ref refs/remotes/origin/HEAD) -replace '^refs/remotes/origin/',''
```

### 判定 merged（用於 `archive/merged/`）

```bash
git merge-base --is-ancestor origin/<branch> origin/$MAIN && echo "merged"
git log --oneline origin/$MAIN..origin/<branch>
```

**規則：** 只有確認 merged 才用 `archive/merged/`；不確定則用 `archive/abandoned/` 或**暫不動**。

### 列出可歸檔候選

```bash
git branch -r --merged "origin/$MAIN" \
  | grep -v "origin/$MAIN\|origin/HEAD\|origin/archive/"
```

---

## 操作步驟

### A. 僅本地（無 `origin/<branch>`）

```bash
git branch -m <old> archive/merged/<old>
```

### B. 遠端存在（改名或歸檔）

```bash
git push origin origin/<old>:refs/heads/<new>
git push origin --delete <old>
```

`<new>` 例：`archive/merged/try-db-layout`。

有追蹤該分支的工作樹時，先改本地再推。

---

## Agent 注意

- 歸檔遠端分支屬破壞性操作：須使用者觸發句或明確同意。
- 更新 `doc/branches.md` 與歸檔一起做。
- 不要對 `main` 做 archive rename。
