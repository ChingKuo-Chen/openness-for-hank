# 新專案加入 workspace（onboarding）

> **給人類**：clone 新 TIA 專案 repo 後，用一句話讓 Agent 放找路規則、ONBOARDING、project-local。  
> **給 Agent**：完整步驟見 [templates/add_project_onboarding.md](../templates/add_project_onboarding.md)。

---

## 你要做的事

1. **Create repo** → clone 到與 **`openness-standard` 同層**（例：`pioneerm-automation\<name>\`）
2. Cursor 打開該機台資料夾（可再把 `openness-standard` 加進 workspace；**忘了加也可以**，Agent 會去同層找）
3. 對 Agent 說：

```text
加入專案 <name>
```

例：`加入專案 line-a-tap`

若這台是**新機要寫程式**：同時給 Agent **2～3 個參考程式**路徑。見 [WRITE_PROJECT.md](WRITE_PROJECT.md)。

---

## Agent 會幫你做什麼

| 產出 | 說明 |
|------|------|
| `.cursor/rules/find-standard.mdc` | 即使沒加 standard 進 workspace，也會去磁碟／GitHub 找 |
| `.cursor/rules/project-local.mdc` | 這台 `.ap21`、PLC、standard 路徑 |
| `AGENTS.md` | 指向 standard |
| `doc/ONBOARDING.md` | 進度表；之後問 **「`<name>` 進度為何」** |
| `doc/REFERENCE_PATHS.md` | Portal、DLL、專案檔路徑 |

日常 Openness 跑 **`openness-standard/host/`**，不會在機台裡複製整份工具。

**只改該機台 repo。**

---

## 兩層規範（勿混淆）

| 層 | 在哪 | 用途 |
|----|------|------|
| **專案 `.cursor/rules/`** | 各 TIA repo | **找路 + 路由**：standard 在哪、V21、CPU、DLL |
| **Standard `rules/`** | `openness-standard/rules/` | **法典** — **連結查閱，不整包 copy** |

本入口 repo 的 `.cursor/rules/` 另放會自動載入的作業知識（`tia-hmi-autoload.mdc`、`tia-tools-autoload.mdc`）。  
機台必帶：`find-standard.mdc`（不要刪）。

索引 → [rules/README.md](../rules/README.md)。

---

## 完工後請你檢查（約 2 分鐘）

打開 **`.cursor/rules/project-local.mdc`**：專案定位、TIA V21、CPU、station、DLL、**standard 路徑**。  
有誤直接說要改哪幾項。

建議：`File → Add Folder to Workspace` 選 `openness-standard`。

查進度：`<name> 進度為何` → 讀 `doc/ONBOARDING.md`。

---

## 相關文件（由淺到深）

| 讀者 | 文件 |
|------|------|
| 人類快速入門 | **本檔** |
| Agent 逐步清單 | [templates/add_project_onboarding.md](../templates/add_project_onboarding.md) |
| Cursor rules 修剪 | [plans/cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md) |
| Openness 手冊 | [doc/OPENNESS.md](OPENNESS.md) |
| 必守規範 | [rules/README.md](../rules/README.md) |
| Agent 總入口 | [AGENTS.md](../AGENTS.md) |

---

## 常用觸發句

| 你說 | 結果 |
|------|------|
| `加入專案 foo` | 對 `foo/` 跑完整 onboarding |
| `foo 進度為何` | 讀 `foo/doc/ONBOARDING.md` 回報 ✅/⬜ |
| `加入專案 foo，先給摘要等我確認` | 先列出推斷的 CPU／路徑，確認後再改檔 |
