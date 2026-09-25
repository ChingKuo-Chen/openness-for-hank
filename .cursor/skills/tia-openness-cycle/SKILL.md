---
name: tia-openness-cycle
description: >-
  Run the TIA Portal V21 Openness compile/export evidence loop on a project
  repo. Use when firmware-style flash is requested by mistake, or the user
  asks to compile, verify, 閉環, Openness cycle, implement PLC/host changes,
  or fix HMI compile / Softkey / Discrete alarm errors.
---

# TIA Openness cycle

Read the project [`.cursor/rules/agent-dev-loop.mdc`](agent-dev-loop.mdc) and [`openness-routing.mdc`](openness-routing.mdc) if present. Default V21 templates：[V21/templates/cursor-rules/agent-dev-loop.mdc](../../../V21/templates/cursor-rules/agent-dev-loop.mdc) plus [V21/rules/tia-openness-rules.md](../../../V21/rules/tia-openness-rules.md)。V19 機台改用 `V19/`。

This replaces MCU **build → flash+verify**. Never call isptool, pyocd, or `make install`.

## Quick workflow

1. Confirm `project-local.mdc`: TIA 版本（V21 或 V19）、DLL、CPU、project path。host 在 `V21/host/` 或 `V19/host/`。
2. `implement` the requested change (host and/or SCL as scoped).
3. Run the project compile command from `project-local.mdc`.
4. Judge: 0 error → PASS. Record Evidence.
5. FAIL → fix → compile again. Up to 3 cycles before reporting a blocker.
6. Export only if blocks/DBs changed. **Download to PLC only if the user asked.**

If host still prints `compile not implemented`, report that onboarding is incomplete; do not fake PASS.

HMI compile / Softkey / Discrete 錯：先讀 [V21/doc/OPENNESS_NOTES.md](../../../V21/doc/OPENNESS_NOTES.md) **§12** 與 [V21/doc/HOW-TO-DISCRETE.md](../../../V21/doc/HOW-TO-DISCRETE.md)，再動手。不要逐畫面改 Softkey、不要 `composition.Import`、不要批次 Export GraphicList、不要找 Openness Discrete API。圖在 `Project.Graphics`，不在畫面 XML。

## Report

```text
[Cycle N] compile: OK/FAIL | export: OK/SKIP | judge: PASS/FAIL
Evidence: <summary>
Next: <fix or done>
```

## Skip compile when

User says docs only / 只改檔不編譯 / review only.
