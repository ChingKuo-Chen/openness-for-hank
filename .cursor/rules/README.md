# Cursor rules（openness-standard）

本 repo **有**會自動載入的作業知識（開本資料夾當 workspace 時）：

| 檔 | 用途 |
|----|------|
| [tia-hmi-autoload.mdc](tia-hmi-autoload.mdc) | Basic HMI／Softkey／Discrete |
| [tia-tools-autoload.mdc](tia-tools-autoload.mdc) | host／AllowList／工具路徑 |

機台 repo 另從 [`templates/cursor-rules/`](../../templates/cursor-rules/) 複製 **找路 + 路由**（必含 `find-standard.mdc`）。  
**不要**把空白的 `project-local.mdc.example` 拷進本 standard 的 `.cursor/rules/`（multi-root 會誤載入）。

見 [doc/ADD_PROJECT.md](../../doc/ADD_PROJECT.md)。
