# openness-standard

pioneerm-automation 的 **唯一 standard**。TIA 版本拆成兩個目錄，**不要**再各養一份獨立 repo。

| 目錄 | 用途 |
|------|------|
| **[V21/](V21/)** | 現行 V21 框架（規定、host、Practice） |
| **[V19/](V19/)** | 從 V21 **整包複製**，用同一架構打 Portal V19，邊測邊改 |

機台 `project-local.mdc` 寫 TIA 版本後，工具走對應樹：

- V21：`V21/host/TiaOpennessCheck.exe`（`V21/host/Build.ps1`）
- V19：`V19/host/TiaOpennessCheck.exe`（`V19/host/Build.ps1`）

新電腦：V21 丟 [V21/host/INSTALL.md](V21/host/INSTALL.md)；V19 丟 [V19/host/INSTALL.md](V19/host/INSTALL.md)。

Agent 入口：[AGENTS.md](AGENTS.md)。加入專案／寫新機先問 **2～3 個參考路徑**（可不給，改用 Practice）；用到的摘錄放機台 `reference/`。全隊 skill 在 [`.cursor/skills/`](.cursor/skills/README.md)；開機台用 `<name>.code-workspace`（含本 repo）。V21 人類入門：[V21/doc/WRITE_PROJECT.md](V21/doc/WRITE_PROJECT.md)。
