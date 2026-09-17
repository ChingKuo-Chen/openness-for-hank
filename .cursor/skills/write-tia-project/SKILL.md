---
name: write-tia-project
description: >-
  Start or continue writing a Siemens TIA machine/PLC project. Use when the
  user says 寫新機, 參考程式, 工作副本, HANDOFF, starts editing an .ap21,
  or works on Basic HMI screens, Softkey, Discrete alarms, or HMI compile.
---

# 寫機台

先讀 [doc/WRITE_PROJECT.md](../../../doc/WRITE_PROJECT.md) 與 [rules/tia-write-project.md](../../../rules/tia-write-project.md)、[rules/tia-standing-orders.md](../../../rules/tia-standing-orders.md)。

改 Basic HMI（畫面／Softkey／Discrete／編譯 HMI）時，動手前記得讀 [doc/OPENNESS_NOTES.md](../../../doc/OPENNESS_NOTES.md) **§12** 與 [doc/HOW-TO-DISCRETE.md](../../../doc/HOW-TO-DISCRETE.md)，不要重試那幾條死路。

寫 LAD／SCL 時再讀 [rules/tia-write-program.md](../../../rules/tia-write-program.md)、[rules/tia-lad-spec.md](../../../rules/tia-lad-spec.md)、[doc/LAD_PRACTICE.md](../../../doc/LAD_PRACTICE.md)，並對照 [`Practice/`](../../../Practice/) 範本。

## 必做

1. **建議使用者給 2～3 個參考程式。** 少於 2 個就問。
2. 只開、只改工作副本。不要 Openness attach 參考。
3. 路徑寫進該機 `handoffs/HANDOFF-*.md`（範本 [templates/HANDOFF.md.example](../../../templates/HANDOFF.md.example)）。
4. **讀完參考 → 寫 `doc/notes/REF-*.md`（大綱＋心得）。** 失敗先翻 notes／OPENNESS_NOTES。見 [rules/read-and-note.md](../../../rules/read-and-note.md)。
5. 編譯後存檔，列剩餘（[rules/list-remaining.md](../../../rules/list-remaining.md)）。

## 不要

- 叫使用者去開 TIA（專案沒開就自己開副本）
- 一個 GUI 開兩個專案
- 把 Siemens DLL 或日常 `.ap21` 推進 Git
- 沒說「記得」就把口頭規定寫進本標準並 push
