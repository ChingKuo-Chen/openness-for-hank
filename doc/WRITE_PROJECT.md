# 寫機台專案（給人類）

新開一台、或改現有一台，都先看這份。Agent 細則：[rules/tia-write-project.md](../rules/tia-write-project.md)、[rules/tia-standing-orders.md](../rules/tia-standing-orders.md)。

---

## 開工前請準備

請給 Agent **2～3 個參考程式**（TIA 專案）。例如：上一台整線、給線／捲取、另一條產線。

沒有 2 份就先講還能找哪裡，不要只丟一份就當全部。

另外還要：

| 項目 | 說明 |
|------|------|
| 工作副本 | 桌面上要改的 `.ap21`（只改這個） |
| IO 準則 | C 表／Excel |
| 電路圖 | 若有 |

參考程式**只讀**。不要在 TIA 用 Openness 打開參考（會寫進去）。

---

## 你對 Agent 可以說

```text
這台參考這三個：<路徑1>、<路徑2>、<路徑3>
工作副本在 <桌面.ap21>
```

或：

```text
要寫新機，我只有這兩個參考
```

Agent 應該主動問缺的那一份，而不是自己決定「只用這個」。

讀完參考後 Agent 應寫 `doc/notes/REF-*.md`（大綱＋心得），下次先翻 notes。手冊同理（`OPENNESS_NOTES` 等）。失敗時順序：心得 → 官方手冊 → 整份重讀。

---

## 工作怎麼分

| 層 | 在哪 | 用途 |
|----|------|------|
| 這本說明書 | `openness-standard/rules/` | 全隊必守 |
| 共用工具 | `openness-standard/host/` | Openness exe／Build |
| 這台捷徑 | 機台 repo 的 `.cursor/rules/project-local.mdc` 與 `find-standard.mdc` | 這台路徑、CPU、DLL、怎麼找到 standard |
| 這台進度 | 機台 `handoffs/HANDOFF-*.md` | 已做／還沒做 |

Git 專案還沒 onboard → 再看 [ADD_PROJECT.md](ADD_PROJECT.md)。

寫 LAD／SCL：全隊共用練習庫與坑點見 [LAD_PRACTICE.md](LAD_PRACTICE.md)。
