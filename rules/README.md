# Openness 規範（rules/）

> **本目錄** = 全隊共用的**規範法典**（Openness、分支、出貨、C# / SCL）。  
> **勿**整包 copy 進 TIA 專案 repo；專案用 [`templates/cursor-rules/`](../templates/cursor-rules/) 複製 **路由型** `.mdc`（TIA 版本、DLL、編譯閉環）。  
> 分工說明 → [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md) § 兩層規範 · 修剪 SOP → [plans/cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md)。

**Workspace 假設：** TIA 專案 repo 與 `openness-standard` 同層（`../openness-standard/rules/`）。

---

## 兩層分工（避免衝突）

| 層 | 位置 | 內容 | 維護 |
|----|------|------|------|
| **Automation 標準** | `openness-standard/rules/*.md` | 完整必守規範、範例、禁止項 | 只改 central；各專案**連結**不複製 |
| **專案捷徑** | `<project>/.cursor/rules/*.mdc` | 本專案 TIA 版本、CPU、station、DLL、dev 閉環 | onboarding 修剪；**不放**規範全文 |

**Agent 行為：**

- 改 C# host → 專案 `openness-routing.mdc` + 本目錄 `csharp-openness-style.md`、`tia-openness-rules.md`
- 改 SCL / DB → `scl-style.md` + 本目錄 Openness 禁則
- 查 V21 路徑 → 專案 `project-local.mdc` + [reference/tia_v21.md](../reference/tia_v21.md)

**禁止：** 在專案 `.mdc` 重寫與本目錄矛盾的規則；有變更應改 central 並讓 template 路由指向新版。

---

## 規範索引

### Openness 與出貨

| 檔案 | 何時讀 |
|------|--------|
| [tia-openness-rules.md](tia-openness-rules.md) | 開啟 Portal、編譯、匯出、下載、權限 |
| [repo-dependencies.md](repo-dependencies.md) | 禁止 `include ../openness-standard/`；copy 範本進專案 |
| [release-sop.md](release-sop.md) | 出貨編譯 + `.zap21` archive（Agent） |
| [branch-naming-sop.md](branch-naming-sop.md) | 分支前綴（`try/`、`service/`、`archive/`）與歸檔 rename |

手冊：[doc/OPENNESS.md](../doc/OPENNESS.md) · [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md)

### 程式排版

| 檔案 | 何時讀 |
|------|--------|
| [csharp-openness-style.md](csharp-openness-style.md) | `.cs` Openness host |
| [scl-style.md](scl-style.md) | SCL 源、DB 註解、塊命名 |

專案 template：[openness-routing.mdc](../templates/cursor-rules/openness-routing.mdc)（改 host / 編譯時 Cursor 自動帶入）

---

## 與 `.cursor/rules/` 對照

| 主題 | 專案 `.mdc`（路由） | 本目錄（權威） |
|------|---------------------|----------------|
| TIA 版本 / CPU / station | `project-local.mdc` | —（路徑在 `reference/`） |
| DLL / 編譯 / 匯出 | `openness-routing.mdc` | `tia-openness-rules.md` |
| 改碼閉環 | `agent-dev-loop.mdc` | `release-sop.md`（出貨時） |
| Git 分支命名與歸檔 | — | `branch-naming-sop.md` |
| C# host | `openness-routing.mdc` | `csharp-openness-style.md` |
| SCL / DB | — | `scl-style.md` |
| Vendor / 產生檔 | `skip-vendor-tree.mdc` | `repo-dependencies.md` |
| Session 備份 | `session-sync.mdc` | — |

---

## 相關入口

| 文件 | 說明 |
|------|------|
| [AGENTS.md](../AGENTS.md) | Agent 總工作流程 |
| [doc/ADD_PROJECT.md](../doc/ADD_PROJECT.md) | 新專案 onboarding |
| [templates/cursor-rules/](../templates/cursor-rules/) | 複製到專案的 rule 範本 |
| [plans/cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md) | rules 修剪算法 |
