---
name: write-tia-project
description: >-
  Start or continue writing a Siemens TIA machine/PLC project. Use when the
  user says 寫新機, 參考程式, 工作副本, HANDOFF, starts editing an .ap21,
  or works on Basic HMI screens, Softkey, Discrete alarms, or HMI compile.
---

# 寫機台

先讀對應樹的 `doc/WRITE_PROJECT.md` 與 `rules/tia-write-project.md`、`rules/tia-standing-orders.md`（V21 預設 [V21/doc/WRITE_PROJECT.md](../../../V21/doc/WRITE_PROJECT.md)；V19 機台改 `V19/`）。

改 Basic HMI（畫面／Softkey／Discrete／編譯 HMI）時，動手前記得讀 [V21/doc/OPENNESS_NOTES.md](../../../V21/doc/OPENNESS_NOTES.md) **§12** 與 [V21/doc/HOW-TO-DISCRETE.md](../../../V21/doc/HOW-TO-DISCRETE.md)，不要重試那幾條死路。V19 若測出不同行為，寫進 `V19/doc/`。

寫 LAD／SCL 時再讀對應樹的 `rules/tia-write-program.md`、`rules/tia-lad-spec.md`、`doc/LAD_PRACTICE.md`，並對照該樹 `Practice/` 範本。

## 必做

1. **加入專案／寫新機時問使用者 2～3 個參考 TIA 路徑。** 沒給也沒關係：改用該樹 `Practice/`。
2. 實際用到的塊、硬體摘錄、IO 片段放進機台 repo 的 **`reference/`**（不要整包 `.ap19`／`.ap21`）。心得仍寫 `doc/notes/REF-*.md`。
3. 只開、只改工作副本。不要 Openness attach 參考。
4. 路徑寫進該機 `handoffs/HANDOFF-*.md`（範本 [V21/templates/HANDOFF.md.example](../../../V21/templates/HANDOFF.md.example)）。
5. 編譯後存檔，列剩餘（[V21/rules/list-remaining.md](../../../V21/rules/list-remaining.md)）。

## 不要

- 叫使用者去開 TIA（專案沒開就自己開副本）
- 一個 GUI 開兩個專案
- 把 Siemens DLL 或日常 `.ap21` 推進 Git
- 沒說「記得」就把口頭規定寫進本標準並 push
