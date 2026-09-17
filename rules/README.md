# Openness 規範（rules/）

> **本目錄** = 全隊共用的**規範法典**。  
> **勿**整包 copy 進 TIA 專案 repo；專案用 [`templates/cursor-rules/`](../templates/cursor-rules/) 複製 **找路 + 路由** `.mdc`。  
> 分工 → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md) · 修剪 → [plans/cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md)。

機台 repo 與 `openness-standard` **同層**（`../openness-standard/rules/`）。沒加 workspace 時仍用絕對路徑讀本目錄。

---

## 兩層分工

| 層 | 位置 | 內容 |
|----|------|------|
| **Standard** | `openness-standard/rules/*.md` | 完整必守規範 |
| **專案捷徑** | `<project>/.cursor/rules/*.mdc` | 找 standard、本專案路徑、CPU、DLL |

**Agent 行為：**

- 寫機台 → `tia-standing-orders.md`、`tia-write-project.md`、`list-remaining.md`
- 改 LAD／SCL → `tia-write-program.md`、`tia-lad-spec.md`、`scl-style.md`；範本庫 `Practice/`
- 跑 Openness → 本 repo `host/` + `tia-openness-rules.md`
- 查 V21 路徑 → 專案 `project-local.mdc` + [reference/tia_v21.md](../reference/tia_v21.md)

**禁止：** 在專案 `.mdc` 重寫與本目錄矛盾的規則；不要刪機台的 `find-standard.mdc`。

---

## 規範索引

### Openness 與出貨

| 檔案 | 何時讀 |
|------|--------|
| [tia-openness-rules.md](tia-openness-rules.md) | 開啟 Portal、編譯、匯出 |
| [repo-dependencies.md](repo-dependencies.md) | 工具在 standard；禁止 csproj include |
| [release-sop.md](release-sop.md) | 出貨編譯 + `.zap21` |
| [branch-naming-sop.md](branch-naming-sop.md) | `try/`、`service/`、`archive/` |

手冊：[doc/OPENNESS.md](../doc/OPENNESS.md) · [doc/OPENNESS_NOTES.md](../doc/OPENNESS_NOTES.md) · [doc/OPENNESS_PITFALLS.md](../doc/OPENNESS_PITFALLS.md) · [doc/WRITE_PROJECT.md](../doc/WRITE_PROJECT.md) · [host/](../host/)

### 寫機台／寫程式

| 檔案 | 何時讀 |
|------|--------|
| [tia-standing-orders.md](tia-standing-orders.md) | 寫任何西門子機台 |
| [tia-write-project.md](tia-write-project.md) | 新機、IO、HMI、網路 |
| [read-and-note.md](read-and-note.md) | 讀完寫心得 |
| [tia-write-program.md](tia-write-program.md) | LAD／SCL 到 0 錯 |
| [tia-lad-spec.md](tia-lad-spec.md) | LadWriter 規格 |
| [list-remaining.md](list-remaining.md) | 做完列剩餘 |

練習庫：[doc/LAD_PRACTICE.md](../doc/LAD_PRACTICE.md) · [`Practice/`](../Practice/)

### 程式排版

| 檔案 | 何時讀 |
|------|--------|
| [csharp-openness-style.md](csharp-openness-style.md) | `.cs` Openness host |
| [scl-style.md](scl-style.md) | SCL／DB |

---

## 相關入口

| 文件 | 說明 |
|------|------|
| [AGENTS.md](../AGENTS.md) | Agent 總流程 |
| [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md) | 新專案 onboarding |
| [templates/cursor-rules/](../templates/cursor-rules/) | 複製到機台的 rule 範本 |
