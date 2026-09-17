---
name: tia-openness-cycle
description: >-
  Run the TIA Portal V21 Openness compile/export evidence loop on a project
  repo. Use when firmware-style flash is requested by mistake, or the user
  asks to compile, verify, 閉環, Openness cycle, implement PLC/host changes,
  or fix HMI compile / Softkey / Discrete alarm errors.
---

# TIA Openness cycle

Read the project [`.cursor/rules/agent-dev-loop.mdc`](agent-dev-loop.mdc) and [`openness-routing.mdc`](openness-routing.mdc) if present; otherwise use [templates/cursor-rules/agent-dev-loop.mdc](../../../templates/cursor-rules/agent-dev-loop.mdc) plus [rules/tia-openness-rules.md](../../../rules/tia-openness-rules.md).

This replaces MCU **build → flash+verify**. Never call isptool, pyocd, or `make install`.

## Quick workflow

1. Confirm `project-local.mdc`: V21, DLL, CPU, project path.
2. `implement` the requested change (host and/or SCL as scoped).
3. Run the project compile command from `project-local.mdc`.
4. Judge: 0 error → PASS. Record Evidence.
5. FAIL → fix → compile again. Up to 3 cycles before reporting a blocker.
6. Export only if blocks/DBs changed. **Download to PLC only if the user asked.**

If host still prints `compile not implemented`, report that onboarding is incomplete; do not fake PASS.

HMI compile / Softkey / Discrete 錯：先讀 [doc/OPENNESS_NOTES.md](../../../doc/OPENNESS_NOTES.md) **§12** 與 [doc/HOW-TO-DISCRETE.md](../../../doc/HOW-TO-DISCRETE.md)，再動手。不要逐畫面改 Softkey、不要 `composition.Import`、不要批次 Export GraphicList、不要找 Openness Discrete API。

## Report

```text
[Cycle N] compile: OK/FAIL | export: OK/SKIP | judge: PASS/FAIL
Evidence: <summary>
Next: <fix or done>
```

## Skip compile when

User says docs only / 只改檔不編譯 / review only.
