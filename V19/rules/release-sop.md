# 出貨 SOP（TIA archive）

> 對齊 edge 的 `release-sop.md` 結構；產物改為 **TIA V21 編譯通過 + `.zap21`**，不是 `make release` 的 `.bin`。

---

## 目的

確保出貨工程：

1. **編譯 0 error**（V21 Openness 或 Portal 編譯）
2. **版號**可追溯（`VERSION` 檔 + tag `v<x.y.z>`）
3. **訂單**可追溯（訂單 tag，註記客戶與機台）
4. **產物**為 `release/<name>_v<x.y.z>.zap21`（是否入 Git 由專案 `project-local.mdc` 決定；預設 **不提交** 大型 archive，只提交 VERSION 與出貨紀錄）

日常開發用 Openness compile 閉環；**量產／交付一律走本 SOP**。

---

## 適用範圍

| 專案類型 | 是否適用 |
|----------|----------|
| 已有 TIA V21 專案 + Openness host | **是** |
| 僅文件、尚無 `.ap21` | 否（先完成 onboarding） |
| `tia-openness-cursor-new` 本身 | **否** |

---

## 前置條件

- [ ] TIA Portal **V21** 已安裝；Openness 已授權（[tia-openness-rules.md](tia-openness-rules.md)）
- [ ] 專案根有 **`VERSION`**（`ver.sub.rev`，納入 Git）
- [ ] `project-local.mdc` 已填 CPU、station、專案路徑
- [ ] git working tree 乾淨或僅含預期變更
- [ ] 使用者同意本輪產出貨 archive（大檔）

---

## 流程

```
release
  │
  ├─ 1. 讀 VERSION；與使用者確認是否 bump
  ├─ 2. Openness compile 目標 PLC（0 error）
  ├─ 3. 匯出可讀清單（塊列表、硬體、必要 SCL）到 doc/ 或 export/
  ├─ 4. Portal / Openness 產 .zap21 到 release/
  ├─ 5. 寫 doc/RELEASE_NOTES.md 摘要（版號、編譯結果、PLC 型號）
  └─ 6. git tag v<x.y.z>；訂單另打訂單 tag
         commit / push 僅在使用者要求時
```

**禁止**把未編譯通過的專案當成出貨。  
**禁止**用 V15/V16 archive 冒充 V21。

---

## 指令對照

| 動作 | 用途 |
|------|------|
| 日常 compile | 開發閉環（見 `agent-dev-loop.mdc`） |
| 本 SOP | **出貨**（版號 + archive + 紀錄） |
| Download to PLC | **不是**出貨預設；須使用者明說 |

---

## 回報

```text
VERSION: x.y.z
compile: OK (0 error, N warning)
archive: release/<file>.zap21
tag: v<x.y.z> （若已打）
```
