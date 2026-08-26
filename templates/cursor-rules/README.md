# Cursor rules 範本（TIA Openness）

onboarding 時 **複製** 本目錄到 `<project>/.cursor/rules/`，再依 [plans/cursor_rules_trim_sop.md](../../plans/cursor_rules_trim_sop.md) 修剪。

| 檔案 | alwaysApply | 用途 |
|------|:-----------:|------|
| [agent-dev-loop.mdc](agent-dev-loop.mdc) | 是 | compile → 證據 |
| [openness-routing.mdc](openness-routing.mdc) | 是 | DLL、編譯／匯出入口 |
| [project-local.mdc.example](project-local.mdc.example) | 是 | 複製為 `project-local.mdc` 後必填 |
| [skip-vendor-tree.mdc](skip-vendor-tree.mdc) | 否（glob） | 勿改 Siemens / 產生檔 |
| [session-sync.mdc](session-sync.mdc) | 否 | 跨機 chat 備份 |

**禁止**把 `openness-standard/rules/*.md` 全文貼進這些檔。
