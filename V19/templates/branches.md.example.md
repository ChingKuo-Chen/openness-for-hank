# <project-name> 分支對照

**本表最後更新：** YYYY-MM-DD（對應 `origin/<default-branch>` @ `<short-sha>`）

日常開發請用 **`<default-branch>`**。**勿 checkout `archive/*`**。`try/*` = 試做；`service/*` = 售後維護。下表以 **GitHub 遠端**（`git fetch origin --prune` 後的 `origin/*`）為準。

## 分支一覽（活躍）

| 分支 | 最新 commit | 最後更新 | 用途 / 狀態 |
|------|-------------|----------|-------------|
| **`<default-branch>`** ⭐ | `<sha>` | YYYY-MM-DD | 預設主線（`origin/HEAD`） |
| `try/<topic>` | `<sha>` | YYYY-MM-DD | （試做主題） |
| `service/<customer>` | `<sha>` | YYYY-MM-DD | （售後維護線） |

## 已歸檔

命名與操作：[openness-standard/rules/branch-naming-sop.md](../../openness-standard/rules/branch-naming-sop.md)

| 分支 | 最新 commit | 最後更新 | 原用途 / 歸檔原因 |
|------|-------------|----------|-------------------|
| `archive/merged/<old-branch>` | `<sha>` | YYYY-MM-DD | （已 merge 進主線） |
| `archive/abandoned/<old-branch>` | `<sha>` | YYYY-MM-DD | （未 merge） |

## 最近 `origin/<default-branch>` 改動（摘要）

| 日期 | Commit | 說明 |
|------|--------|------|
| YYYY-MM-DD | `<sha>` | （簡述） |

## 維護本表

活躍分支：

```powershell
git fetch origin --prune
git for-each-ref refs/remotes/origin --format="%(refname:short)|%(objectname:short)|%(committerdate:short)|%(subject)"
```

更新表格後，請一併修改文首 **本表最後更新**。

通則：[openness-standard/doc/BRANCHES_MD.md](../../openness-standard/doc/BRANCHES_MD.md)
