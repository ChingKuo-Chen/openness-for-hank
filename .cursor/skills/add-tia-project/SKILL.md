---
name: add-tia-project
description: >-
  Onboard a TIA Portal Openness project repo into the pioneerm-automation
  workspace. Use when the user says 加入專案, add project, onboarding a TIA
  repo, or asks to copy openness-standard cursor rules / find-standard.
---

# Add TIA project

Read [AGENTS.md](../../../AGENTS.md) and [doc/ADD_PROJECT.md](../../../doc/ADD_PROJECT.md) first. Follow [templates/add_project_onboarding.md](../../../templates/add_project_onboarding.md) in order.

## Quick workflow

1. Find the machine repo. Find **openness-standard** with [find-standard.mdc](../../../templates/cursor-rules/find-standard.mdc) (workspace → sibling → clone). Do **not** stop just because standard is not in the workspace.
2. Resolve **TIA V21** DLL with [reference/tia_v21.md](../../../reference/tia_v21.md). Do not invent V16 paths.
3. **Do not** copy [templates/host/](../../../templates/host/) into the machine repo. Daily tools stay in this standard `host/`.
4. Copy [templates/cursor-rules/](../../../templates/cursor-rules/) → `<name>/.cursor/rules/` (**keep** `find-standard.mdc`) and [templates/AGENTS.md.example](../../../templates/AGENTS.md.example) → `<name>/AGENTS.md`. Trim with [plans/cursor_rules_trim_sop.md](../../../plans/cursor_rules_trim_sop.md).
5. Write `<name>/doc/ONBOARDING.md` and `doc/REFERENCE_PATHS.md`. Put the standard root path in `project-local.mdc`.
6. Ask the user to check `.cursor/rules/project-local.mdc`. Suggest Add Folder for `openness-standard`.

## Rules

- Only edit `<name>/`. Do not sweep other workspace folders.
- Do not copy `rules/*.md` law into `.mdc` files.
- Do not implement full Openness compile API in this skill.
- Do not add Siemens DLLs or `.ap21` to Git.

## Example user prompts

- `加入專案 line-a-tap`
- `加入專案 line-a-tap，先給摘要等我確認`
- `line-a-tap 進度為何`
