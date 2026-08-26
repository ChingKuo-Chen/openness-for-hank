---
name: add-tia-project
description: >-
  Onboard a TIA Portal Openness project repo into the pioneerm-automation
  workspace. Use when the user says 加入專案, add project, onboarding a TIA
  repo, or asks to copy openness-standard cursor rules / host skeleton.
---

# Add TIA project

Read [AGENTS.md](../../../AGENTS.md) and [doc/ADD_PROJECT.md](../../../doc/ADD_PROJECT.md) first. Follow [templates/add_project_onboarding.md](../../../templates/add_project_onboarding.md) in order.

## Quick workflow

1. Confirm `<name>/` is in the Cursor workspace. If not, stop and ask to Add Folder.
2. Resolve **TIA V21** DLL with [reference/tia_v21.md](../../../reference/tia_v21.md). Do not invent V16 paths.
3. Copy [templates/host/](../../../templates/host/) → `<name>/host/` if missing ([repo-dependencies.md](../../../rules/repo-dependencies.md): copy, never include this repo).
4. Copy [templates/cursor-rules/](../../../templates/cursor-rules/) → `<name>/.cursor/rules/` and trim with [plans/cursor_rules_trim_sop.md](../../../plans/cursor_rules_trim_sop.md). No placeholders left.
5. Write `<name>/doc/ONBOARDING.md` and `doc/REFERENCE_PATHS.md` from templates.
6. Ask the user to check `.cursor/rules/project-local.mdc`.

## Rules

- Only edit `<name>/`. Do not sweep other workspace folders.
- Do not copy `rules/*.md` law into `.mdc` files.
- Do not implement full Openness compile API in this skill; leave host skeleton and paths.
- Do not add Siemens DLLs or `.ap21` to Git.

## Example user prompts

- `加入專案 line-a-tap`
- `加入專案 line-a-tap，先給摘要等我確認`
- `line-a-tap 進度為何`
