# openness-standard

pioneerm-automation 的 **唯一 standard**：規定、寫機流程、Openness `host/`、小工具、換電腦 `INSTALL.md`。對齊 edge `firmware-standard` 排版，內容是 **TIA Portal V21 Openness**（不是 MCU）。

**新電腦：把 [host/INSTALL.md](host/INSTALL.md) 丟給 Cursor。** 腳本在同一層：`host/Build.ps1`、`host/GrantAllowListAccess.ps1`。

日常 workspace：**本 repo + 正在寫的機台 repo**。忘了把本 repo 加進 workspace 也沒關係——機台裡的 `find-standard.mdc` 會去同層找或 clone。

## 寫新機台時先做這件事

Agent **要建議使用者給 2～3 個參考程式**。參考只讀；只改副本。不要 Openness attach 參考。

人類入門：[doc/WRITE_PROJECT.md](doc/WRITE_PROJECT.md)

## 新專案

clone 機台到與本 repo **同層** → 對 Agent 說：

```text
加入專案 <repo-name>
```

不必先 Add Folder 本 repo。Agent 會放 `find-standard.mdc`、`project-local.mdc`、ONBOARDING。完工請檢查 `project-local.mdc`。

| 讀什麼 | 路徑 |
|--------|------|
| **寫機台** | **[doc/WRITE_PROJECT.md](doc/WRITE_PROJECT.md)** |
| **加入 Git 專案** | [doc/ADD_PROJECT.md](doc/ADD_PROJECT.md) |
| 查進度 | `<repo> 進度為何` → 該 repo 的 `doc/ONBOARDING.md` |

## 目錄

| 目錄 | 內容 |
|------|------|
| [doc/](doc/) | 手冊：寫機、加入專案、Openness、LAD 練習、踩雷 |
| [rules/](rules/) | 必守規範 |
| [host/](host/) | 實戰 Openness（`Build.ps1`、`*.cs`、INSTALL） |
| [tools/](tools/) | LAD／GUI／IO |
| [Practice/](Practice/) | 已驗證 LAD／SCL 範本（不含某一台測試機） |
| [reference/](reference/) | TIA V21 路徑；官方 PDF |
| [templates/](templates/) | 複製進機台的薄層（含 `find-standard.mdc`） |
| [plans/](plans/) | 設計原則、onboarding 修剪 |

## 設計原則

1. **TIA 專案是源** — 硬體、塊、DB 以 Portal 為準。見 [plans/PRINCIPLES.md](plans/PRINCIPLES.md)。
2. **工具只在本 repo** — 機台不 copy 實戰 host。見 [rules/repo-dependencies.md](rules/repo-dependencies.md)。
3. **兩層規範** — 法典在 `rules/`；機台只放找路與路由 `.mdc`。
4. **鎖定 TIA Portal V21**。

## 不做什麼

- 不搬 MCU、不把 Siemens DLL 或日常 `.ap21` 推進 Git
- 不把某一台的 HANDOFF／`DSP_TCP_test` 測試規格當全員範本
- 不把 `rules/*.md` 全文貼進各專案 `.mdc`

舊包：`tia-openness-cursor-new`、`tia-openness-cursor` 已改為來源／archive，不要當入口。
