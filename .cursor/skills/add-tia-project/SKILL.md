---
name: add-tia-project
description: >-
  Onboard a TIA Portal Openness project repo into the pioneerm-automation
  workspace. Use when the user says 加入專案, add project, onboarding a TIA
  repo, or asks to copy openness-standard cursor rules / find-standard.
---

# Add TIA project

Read [AGENTS.md](../../../AGENTS.md) first. TIA V21 走 [V21/doc/ADD_PROJECT.md](../../../V21/doc/ADD_PROJECT.md)；V19 走 [V19/](../../../V19/) 對應檔。Follow [templates/add_project_onboarding.md](../../../V21/templates/add_project_onboarding.md) in order（V19 機台改用 `V19/templates/`）。

## Quick workflow

1. Find the machine repo. Find **openness-standard** with [find-standard.mdc](../../../V21/templates/cursor-rules/find-standard.mdc) (workspace → sibling → clone). Do **not** stop just because standard is not in the workspace.
2. Resolve DLL from `project-local` TIA version：V21 → [V21/reference/tia_v21.md](../../../V21/reference/tia_v21.md)；V19 → [V19/reference/tia_v19.md](../../../V19/reference/tia_v19.md). Do not invent V16 paths.
3. **Do not** copy [templates/host/](../../../V21/templates/host/) into the machine repo. Daily tools stay in `V21/host/` or `V19/host/`.
4. Copy matching tree `templates/cursor-rules/` → `<name>/.cursor/rules/` (**keep** `find-standard.mdc`) and `templates/AGENTS.md.example` → `<name>/AGENTS.md`. Trim with [plans/cursor_rules_trim_sop.md](../../../V21/plans/cursor_rules_trim_sop.md).
5. Write `<name>/doc/ONBOARDING.md` and `doc/REFERENCE_PATHS.md`. Put the standard root path in `project-local.mdc`.
6. **Ask for 2～3 read-only TIA project paths** (previous line, similar station, another plant). Optional: if the user has none, use that TIA tree’s `Practice/` library. Copy `templates/reference/README.md.example` → `<name>/reference/README.md`. When you actually copy logic from a reference, put **only those excerpts** under `<name>/reference/` — never attach the reference with Openness, never commit `.ap19`/`.ap21`.
7. Write `<name>/<name>.code-workspace` from `templates/project.code-workspace.example`（folders: 機台 `.` + `../openness-standard`）. Ask the user to **File → Open Workspace** that file so team skills in `openness-standard/.cursor/skills/` autoload. **Do not** copy those skills into the machine repo.
8. Ask the user to check `.cursor/rules/project-local.mdc`.

## Rules

- Only edit `<name>/`. Do not sweep other workspace folders.
- Do not copy `rules/*.md` law into `.mdc` files.
- Do not copy `openness-standard/.cursor/skills/` into the machine repo.
- Team-wide skills go in `openness-standard/.cursor/skills/` (see that folder’s README). Not `~/.cursor/skills/`, not the machine repo.
- Do not implement full Openness compile API in this skill.
- Do not add Siemens DLLs or `.ap21` to Git.

## Example user prompts

- `加入專案 line-a-tap`
- `加入專案 line-a-tap，先給摘要等我確認`
- `line-a-tap 進度為何`
