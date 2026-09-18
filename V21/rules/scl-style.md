# SCL 與 DB 風格

適用從 Openness 匯出／匯入的 SCL、DB 介面註解。不是 C# host（見 [csharp-openness-style.md](csharp-openness-style.md)）。

---

## 命名

| 種類 | 慣例 |
|------|------|
| FB / FC | `PascalCase` 或既有產線前綴（與 TIA 專案一致，**不要**為了 Git 改已出貨塊名） |
| DB | `DB_<用途>` 或專案既有編號；編號變更須寫進 `doc/` |
| 變數 | 對齊現有專案；新變數用可讀名稱，避免 `temp1` |

---

## 註解

- 塊標題：用途、掃描角色（cyclic / 一次性）、與哪台 CPU
- 介面區（VAR_INPUT / OUTPUT / IN_OUT）：單位、範圍、誰寫入
- 不要把整份機械規格貼進 SCL

---

## 與 Openness 的關係

- Git 裡的 SCL 是**可 diff 的真相**；Portal 內編譯結果以 V21 compile 為準
- 匯入覆蓋既有塊前必須有使用者同意或專案計劃明文
- 優化塊（optimized DB）的偏移不可當 Modbus 地圖；需非優化或明確轉換表時寫在 `doc/`

---

## 禁止

- 在 SCL 裡依賴未定義的 MCU 標頭／C 巨集
- 把 isptool / UART CLI 指令寫進 PLC 註解當操作手冊（操作手冊放 `doc/`）
