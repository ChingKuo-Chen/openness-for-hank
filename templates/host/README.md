# Openness host 骨架

複製整個 `host/` 到 TIA 專案 repo，**不要** ProjectReference 回 `openness-standard`。

見 [rules/repo-dependencies.md](../../rules/repo-dependencies.md)。

1. 改 `OpennessHost.csproj` 的 HintPath 為本機 V21 `Siemens.Engineering.dll`
2. 驗證命令寫進 `.cursor/rules/project-local.mdc`
3. 第一波只要求能建置（本機有 DLL 時）與列出「尚未實作 compile」；完整 Openness API 呼叫之後再補
