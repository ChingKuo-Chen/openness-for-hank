# TIA 專案 Repo 依賴規範

TIA 專案 **clone 單一 repo 即可還原工程與 host**。`openness-standard` 僅供 Agent / 規範，**不得**作為建置期 sibling 依賴。

---

## 允許的 sibling 路徑

| 路徑 | 用途 | 備註 |
|------|------|------|
| `../openness-standard` | Agent 讀規範、複製範本 | **執行期／建置期都不要 reference** |
| TIA Portal V21 PublicAPI（本機 Program Files） | `Siemens.Engineering.dll` | 不進 Git；路徑寫在 `project-local.mdc` |

除上述外，**禁止**在 `.csproj`、腳本、Makefile 使用 `../<其他 repo>/` 當建置輸入。

---

## 禁止：建置依賴 openness-standard

```xml
<!-- 禁止 -->
<ProjectReference Include="..\openness-standard\templates\host\OpennessHost.csproj" />
```

```csharp
// 禁止
// 從 ../openness-standard/templates/host/Program.cs 當編譯來源
```

工程師不必 clone `openness-standard` 也能還原專案（TIA + Visual Studio / dotnet 除外）。規範與範本由 Agent 讀取後 **copy 進專案 repo**。

---

## Copy-not-include

1. **複製** [templates/host/](../templates/host/) → `<project>/host/`（納入 Git）
2. **複製** [templates/cursor-rules/](../templates/cursor-rules/) → `<project>/.cursor/rules/` 後修剪
3. `.csproj` 以 HintPath 指向 **本機** V21 PublicAPI，路徑寫在 Directory.Build.props 或 `project-local.mdc` 記載的變數；**不要**把 DLL 勾進 Git

`openness-standard` 更新 host 骨架時，對各專案 `host/` 做 diff 同步，不要改成 include。

---

## 驗收

```text
專案內不得出現：
- ProjectReference / Import 指向 ../openness-standard
- Git 追蹤 Siemens.Engineering*.dll
```

專案 `doc/` 應自包含出貨與 Openness 說明，勿把日常連結寫成「請打開 ../openness-standard/...」當唯一真相（Agent 可讀 central；人類交付文件要在專案內）。
