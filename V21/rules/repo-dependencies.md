# TIA 專案 Repo 依賴規範

日常：**機台一個 repo** + 同層（或 workspace）的 **`openness-standard`**。  
工具、規定、LAD 範本只維護在 standard。機台 repo 放這台的 HANDOFF、C 表、規格與路由 `.mdc`。

---

## 允許的 sibling 路徑

| 路徑 | 用途 | 備註 |
|------|------|------|
| `../openness-standard` | Agent **讀**規定、**跑** `host/` / `tools/` | 執行期用 exe／腳本；**不要**當 `.csproj` 編譯輸入 |
| TIA Portal V21 PublicAPI（本機 Program Files） | Openness DLL | 不進 Git；路徑寫在 `project-local.mdc` |

除上述外，**禁止**在 `.csproj`、Makefile 使用 `../<其他 repo>/` 當建置輸入。

---

## 日常：工具只在 standard

寫機、編譯、匯入區塊：

```text
<openness-standard>/host/TiaOpennessCheck.exe
<openness-standard>/host/Build.ps1
```

機台 **不要**再 copy 整份 `host/*.cs` 當日常工具（會分叉）。  
`templates/host/` 只作出貨／對方沒有 standard 時的最小骨架，不是每人一份實戰 host。

找不到 standard：照專案 [`.cursor/rules/find-standard.mdc`](../templates/cursor-rules/find-standard.mdc) 搜尋或 `gh repo clone` 到同層。**不要**因此停工等人加 Folder。

---

## 禁止：建置依賴 standard 源碼

```xml
<!-- 禁止 -->
<ProjectReference Include="..\openness-standard\host\..." />
```

```csharp
// 禁止：把 standard 的 Program.cs 當本專案編譯來源
```

---

## Onboarding 要 copy 進機台的（薄層）

1. [templates/cursor-rules/](../templates/cursor-rules/) → `<project>/.cursor/rules/`（**必含** `find-standard.mdc`）
2. [templates/AGENTS.md.example](../templates/AGENTS.md.example) → `<project>/AGENTS.md`（若尚無）
3. 修剪後填 `project-local.mdc`（含 standard 根路徑、`.ap21`、PLC）

**不要**預設把 `templates/host/` 整包 copy 進機台。

---

## 驗收

```text
專案內不得出現：
- ProjectReference / Import 指向 ../openness-standard 的 C# 工程
- Git 追蹤 Siemens.Engineering*.dll
- 第二份實戰 host/*.cs（與 standard 重複維護）
```

專案 `doc/` 出貨說明要自含；Agent 讀規定仍以 standard 為準。
