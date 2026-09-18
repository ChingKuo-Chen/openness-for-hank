# 設計因果 — Openness 計劃共通原則

> 舊 edge MCU repo 只拿來**對齊規範排版**，不代表本 org 應以韌體或 C# **反推 PLC 設計**。  
> **TIA 專案是源；Openness host 是結果。**

---

## 順序（不可顛倒）

```
產線／機台需求（I/O、動作、安全）
    ↓
TIA Portal V21 硬體組態 + 程式塊 + DB
    ↓  本機 .ap21
Openness host（C#）開啟／編譯／匯出
    ↓
Git 內可 diff 的 SCL／文件／host
    ↓  出貨
.zap21 archive + VERSION
```

**一句話：先有 TIA 專案，才有自動化腳本，才有出貨 archive。**

---

## 新專案流程

```
開 GitHub repo（pioneerm-automation）
  → clone 到與 openness-standard 同層
  → 加入 Cursor workspace
  → 「加入專案 <name>」
  → 填 project-local（V21、CPU、station、DLL）
  → 放或連 .ap21（本機）
  → host 能 compile
  → 再擴充 Openness 功能
```

**禁止**先寫一堆 C# 假想塊名，再要求 Portal 遷就 host。

---

## 對照 edge

| edge | 本 org |
|------|--------|
| KiCad 是硬體源 | TIA 硬體組態是 PLC 源 |
| `core.h` 不是電路依據 | `Program.cs` 不是 PLC 設計依據 |
| flash 閉環 | Openness compile 閉環 |
