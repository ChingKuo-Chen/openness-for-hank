# 新專案加入 workspace（onboarding）

> **給人類**：clone 新 TIA 專案 repo 並加入 Cursor workspace 後，用一句話讓 Agent 幫你放 Cursor rules、ONBOARDING、Openness host 骨架。  
> **給 Agent**：完整步驟見 [templates/add_project_onboarding.md](../templates/add_project_onboarding.md)。

---

## 你要做的事（3 步）

1. **Create repo** → clone 到 `~/automation/<name>/`（或與 `openness-standard` 同層）
2. **Cursor**：File → Add Folder to Workspace（選該 repo）
3. 對 Agent 說：

```text
加入專案 <name>
```

例：`加入專案 line-a-tap`

---

## Agent 會幫你做什麼

| 產出 | 說明 |
|------|------|
| `host/` | Openness host 骨架（見 [templates/host/](../templates/host/)），若尚無 |
| `doc/ONBOARDING.md` | 進度表；之後問 **「`<name>` 進度為何」** 可追蹤 |
| `.cursor/rules/` | TIA V21 / DLL / 編譯閉環路由（見 [cursor_rules_trim_sop](../plans/cursor_rules_trim_sop.md)） |
| `doc/REFERENCE_PATHS.md` | 已解析的 Portal、DLL、專案檔路徑 |

**只改該 repo**；不會動 workspace 裡其他專案。

---

## 兩層規範（勿混淆）

| 層 | 在哪 | 用途 |
|----|------|------|
| **專案 `.cursor/rules/`** | 各 TIA repo | **路由**：V21、CPU、station、DLL、dev 閉環 |
| **Automation `rules/`** | `openness-standard/rules/` | **法典**：Openness、分支、排版、出貨 — **連結查閱，不整包 copy** |

**openness-standard 本身不存放可生效的 `.mdc` rules。** 範本在 [`templates/cursor-rules/`](../templates/cursor-rules/)（刻意不在本 repo 的 `.cursor/rules/` 生效，避免 multi-root workspace 誤載入 template）。

索引 → [rules/README.md](../rules/README.md)。

---

## 完工後請你檢查（約 2 分鐘）

Agent 結束時會請你打開：

**`.cursor/rules/project-local.mdc`**

確認：專案定位、TIA V21、CPU、station、DLL 路徑是否正確。  
有誤直接說要改哪幾項，Agent 會更新 rules（必要時同步 `REFERENCE_PATHS.md`）。

查進度：`<name> 進度為何` → 讀 `doc/ONBOARDING.md`。

---

## 相關文件（由淺到深）

| 讀者 | 文件 |
|------|------|
| 人類快速入門 | **本檔** |
| Agent 逐步清單 | [templates/add_project_onboarding.md](../templates/add_project_onboarding.md) |
| Cursor rules 修剪算法 | [plans/cursor_rules_trim_sop.md](../plans/cursor_rules_trim_sop.md) |
| Openness 手冊 | [doc/OPENNESS.md](OPENNESS.md) |
| 必守規範 | [rules/README.md](../rules/README.md) |
| Agent 總入口 | [AGENTS.md](../AGENTS.md) § 加入專案 |

---

## 常用觸發句

| 你說 | 結果 |
|------|------|
| `加入專案 foo` | 對 `foo/` 跑完整 onboarding |
| `foo 進度為何` | 讀 `foo/doc/ONBOARDING.md` 回報 ✅/⬜ |
| `加入專案 foo，先給摘要等我確認` | 先列出推斷的 CPU／路徑，確認後再改檔 |
