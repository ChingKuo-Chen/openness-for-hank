# C# Openness host 排版

`.cs` 為 TIA Openness 主機程式，不是 MCU C。PLC 行為以 Portal 專案為準。細則禁則 → [tia-openness-rules.md](tia-openness-rules.md)。

---

## 檔案順序

1. `using`（System → Siemens.Engineering → 專案命名空間）
2. 檔案內常數 / enum
3. 型別宣告
4. 實作

區塊之間留一行空白。

---

## 命名

| 種類 | 慣例 |
|------|------|
| 公開型別、方法 | PascalCase |
| 區域變數、參數 | camelCase |
| 私有欄位 | `_camelCase` |
| 常數 | PascalCase 或領域縮寫（`PlcName`、`CpuMlfb`） |

Siemens API 型別名稱保持原樣（`Project`、`TiaPortal`、`PlcSoftware`），不要自創同義別名包裝整棵 API。

---

## Openness 資源

- `TiaPortal` / `Project` 用 `using` 或明確 `Dispose`／`Close`
- 不要在靜態建構子裡開 Portal
- 路徑、station 名從設定檔或命令列讀，**不要**在多處 hardcode；單一來源寫在專案 README 或 `host/appsettings.json`（無密鑰）

---

## 錯誤

- 捕獲 `EngineeringTargetInvocationException` 等 Openness 例外時，記錄 **InnerException** 與編譯／開啟上下文
- 不要 `catch { }` 後當成功
- 回傳給 Agent 的訊息要能當 [tia-openness-rules.md](tia-openness-rules.md) 的 Evidence

---

## 註解

- 公開 API：一句話說明「對 Portal 做什麼」
- 不要大段重述 Siemens 文件
- 與 SCL 塊對應的常數，註明 TIA 塊名／DB 編號
