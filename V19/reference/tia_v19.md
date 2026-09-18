# TIA Portal V19

本樹（`openness-standard/V19`）打 **Portal V19**。從 V21 框架複製，路徑以本機實際檔案為準。

---

## 本機已確認（英文 Windows）

| 項目 | 路徑 |
|------|------|
| Portal | `C:\Program Files\Siemens\Automation\Portal V19\` |
| PublicAPI | `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\` |
| Engineering DLL | `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll`（19.0.0.0） |
| HMI DLL | `C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.Hmi.dll` |

V19 **沒有** V21 的 `net48` 四顆（Base / Step7 / WinCC / WinCC.Extension）。host 編譯只 reference 上面兩顆。

---

## 專案副檔名

| 種類 | 副檔名 |
|------|--------|
| 未壓縮專案 | `.ap19` |
| Archive | `.zap19` |

---

## Agent 查路徑（Windows）

```powershell
Get-ChildItem "C:\Program Files\Siemens\Automation\Portal V19\PublicAPI" -Recurse -Filter Siemens.Engineering.dll -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty FullName
```

找不到 → 停，請使用者確認 V19 已裝。不要改用 V21 DLL 開 `.ap19`。
