# TIA Portal V21

pioneerm-automation **鎖定 V21**。各專案把本機實際路徑寫進 `.cursor/rules/project-local.mdc` 與 `doc/REFERENCE_PATHS.md`；本檔是探索用預設值。

---

## 預設安裝位置（英文 Windows）

| 項目 | 典型路徑 |
|------|----------|
| Portal | `C:\Program Files\Siemens\Automation\Portal V21\` |
| PublicAPI 根 | `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\` |
| Engineering DLL | `C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21.0\Siemens.Engineering.dll` |

若本機是 `V21` 而非 `V21.0` 子目錄，**以實際檔案為準**，onboarding 時寫死在專案 paths，不要留「V21.0 或 V21」。

---

## 專案副檔名

| 種類 | 副檔名 |
|------|--------|
| 未壓縮專案 | `.ap21` |
| Archive | `.zap21` |

不要用 V15/V16 的 `.ap15` / `.ap16` 當本 org 預設。

---

## Agent 查路徑（Windows）

```powershell
Get-ChildItem "C:\Program Files\Siemens\Automation\Portal V21\PublicAPI" -Recurse -Filter Siemens.Engineering.dll -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty FullName
```

找不到 → 停，請使用者確認 V21 已裝，不要改用舊版 DLL。

---

## 注意

- Openness 授權在 Portal 內設定，不是 Git 能解
- DLL **不進 Git**
- 雲端 CI 無 Portal，不能當編譯機
