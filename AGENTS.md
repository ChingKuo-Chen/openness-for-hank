# Agent 工作指引 — TIA Openness 與寫機台

本 repo（**`openness-standard`**）是 **唯一 standard**。內容分成兩棵樹：

| 樹 | 路徑 | 何時用 |
|----|------|--------|
| **V21** | [`V21/`](V21/) | `project-local.mdc` 寫 TIA V21 / `.ap21` |
| **V19** | [`V19/`](V19/) | `project-local.mdc` 寫 TIA V19 / `.ap19` |

V19 是 **V21 框架的複本**，用同一套 host／rules／Practice 去打 Portal V19，編譯失敗就改 V19 那棵，**不要**改 V21 去遷就 V19。

詳細指引讀對應樹的 `AGENTS.md`：

- [V21/AGENTS.md](V21/AGENTS.md)
- [V19/AGENTS.md](V19/AGENTS.md)

## 找工具

先讀機台 [`.cursor/rules/project-local.mdc`](templates/cursor-rules/project-local.mdc.example) 的 TIA 版本（機台 repo 裡那份）。

| TIA | host | INSTALL |
|-----|------|---------|
| V21 | `V21/host/`（`Build.ps1`、`TiaOpennessCheck.exe`） | `V21/host/INSTALL.md` |
| V19 | `V19/host/` | `V19/host/INSTALL.md` |

**禁止**用 V21 exe 打開 `.ap19`，也禁止用 V19 exe 打開 `.ap21`。

## 其餘

使用者用自然語言即可。法典在各樹的 `rules/`，不要 copy 進機台 `.mdc`。

每則對話仍自動載入根目錄 `.cursor/rules/tia-hmi-autoload.mdc` 與 `tia-tools-autoload.mdc`。HMI 實測筆記以 **V21** 為準；V19 行為以 V19 樹裡測出來的為準。

新 clone 確認清單：[V21/doc/CLONE-CONFIRM.md](V21/doc/CLONE-CONFIRM.md)。使用者回 **「我看過了」** 之前，不要開始改機台或跑 Openness。

寫新機／加入專案：**先問 2～3 個參考路徑**（可不給，改用 Practice）。用到的摘錄放機台 `reference/`。見對應樹的 `doc/WRITE_PROJECT.md`。

全隊 Cursor skill 只放本 repo [`.cursor/skills/`](.cursor/skills/README.md)。加入專案時建立 `<name>.code-workspace`（機台 + `../openness-standard`），開這個 workspace 才會自動掛上 skill。不要把 skill copy 進機台，也不要只放在 `~/.cursor/skills/`。
