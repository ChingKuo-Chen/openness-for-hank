# openness-standard

pioneerm-automation 的 **Agent 入口**、規範與共用範本。對齊 edge 的 `firmware-standard` 排版，內容改為 **TIA Portal V21 Openness**（不是 MCU）。

## 新專案加入 workspace（onboarding）

clone 新 TIA 專案 repo → **Add Folder to Workspace** → 對 Agent 說：

```text
加入專案 <repo-name>
```

Agent 會建立 `.cursor/rules/`、`doc/ONBOARDING.md`、`doc/REFERENCE_PATHS.md`，以及 Openness host 骨架（若尚無）。  
**完工後請你檢查** `.cursor/rules/project-local.mdc`（TIA 版本、CPU、station、DLL 路徑）。

| 讀什麼 | 路徑 |
|--------|------|
| **人類入門（從這裡開始）** | **[doc/ADD_PROJECT.md](doc/ADD_PROJECT.md)** |
| Agent 逐步清單 | [templates/add_project_onboarding.md](templates/add_project_onboarding.md) |
| Cursor rules 修剪 SOP | [plans/cursor_rules_trim_sop.md](plans/cursor_rules_trim_sop.md) |
| 查進度 | `<repo> 進度為何` → 讀該 repo 的 `doc/ONBOARDING.md` |

計畫總索引 → [plans/README.md](plans/README.md)

---

## 你可以直接下指令

```
加入專案 line-a-tap
line-a-tap 進度為何
line-a-tap 幫我寫分支對照表
line-a-tap 歸檔分支 try/openness-compile
```

Agent 讀 [AGENTS.md](AGENTS.md) 與 `.cursor/skills/` 執行。

## 目錄

| 目錄 | 內容 |
|------|------|
| [doc/](doc/) | **手冊** — ADD_PROJECT、OPENNESS、BRANCHES |
| [rules/](rules/) | **必守規範** — Openness、分支、出貨、C# / SCL 排版 |
| [reference/](reference/) | **TIA V21** 路徑與注意事項 |
| [plans/](plans/) | 設計原則、onboarding 修剪 SOP |
| [templates/](templates/) | 可 copy 進專案 repo 的範本 |
| [templates/cursor-rules/](templates/cursor-rules/) | **Cursor rules 範本** — 新專案複製 `.cursor/rules/` |
| `.cursor/skills/` | `add-tia-project`、`tia-openness-cycle` |

## 設計原則

1. **TIA 專案是源** — PLC 硬體、程式塊、DB 以 Portal 專案為準；C# Openness host 是自動化，不是設計來源。見 [plans/PRINCIPLES.md](plans/PRINCIPLES.md)。
2. **專案 repo 自包含** — clone 單一 repo 即可還原工程；禁止建置期 `include ../openness-standard/`。見 [rules/repo-dependencies.md](rules/repo-dependencies.md)。
3. **兩層規範** — 法典在本 repo `rules/`；專案只放路由型 `.mdc`。見 [rules/README.md](rules/README.md)。
4. **鎖定 TIA Portal V21** — DLL / 副檔名 / 編譯閉環見 [reference/tia_v21.md](reference/tia_v21.md)。

## 手冊（doc/）

| 文件 | 說明 |
|------|------|
| [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md) | 新專案 onboarding 入門 |
| [doc/OPENNESS.md](doc/OPENNESS.md) | Openness 主機、權限、編譯／匯出 |
| [doc/BRANCHES_MD.md](doc/BRANCHES_MD.md) | 各專案 `doc/branches.md` 通則 |

## 規範（rules/）

> 索引與 `.cursor/rules/` 分工 → **[rules/README.md](rules/README.md)**

| 文件 | 說明 |
|------|------|
| [rules/tia-openness-rules.md](rules/tia-openness-rules.md) | Openness P0：開啟、編譯、匯出、禁則 |
| [rules/branch-naming-sop.md](rules/branch-naming-sop.md) | `try/`、`service/`、`archive/` |
| [rules/repo-dependencies.md](rules/repo-dependencies.md) | 專案自包含 |
| [rules/release-sop.md](rules/release-sop.md) | 出貨：編譯通過 + archive |
| [rules/csharp-openness-style.md](rules/csharp-openness-style.md) | C# Openness host 排版 |
| [rules/scl-style.md](rules/scl-style.md) | SCL / DB 註解與命名 |

## 參考（reference/）

| 文件 | 說明 |
|------|------|
| [reference/tia_v21.md](reference/tia_v21.md) | V21 安裝路徑、PublicAPI、`.ap21` / `.zap21` |

## 不做什麼

- 不搬 MCU：ISR、WDT、UART CLI、pyocd、isptool、TRM、KiCad
- 不把 Siemens DLL 或 TIA 安裝檔推進 Git
