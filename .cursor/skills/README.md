# 全隊 Cursor Skills

全隊都用得到的 skill **只寫在這裡**：`openness-standard/.cursor/skills/<name>/SKILL.md`。

Cursor 會從 **workspace 根目錄** 的 `.cursor/skills/` 自動發現。機台 repo 只要用 `.code-workspace` 把本資料夾加進去，大家就會掛上同一套，不必每人把 skill 裝到 `~/.cursor/skills/`。

## 現有

| Skill | 何時用 |
|-------|--------|
| `add-tia-project` | 加入專案、onboarding |
| `write-tia-project` | 寫新機、HANDOFF、Basic HMI |
| `tia-openness-cycle` | 編譯／閉環、誤用 flash |

## 規則

- **全隊都用** → 加進本目錄（commit 進 `openness-standard`），不要只放自己的 `~/.cursor/skills/`。
- **只有一台機** → 不要放這裡；寫在該機 `doc/` 或 HANDOFF。
- **不要 copy** 進機台 `.cursor/skills/`（相對路徑會指錯；workspace 開著時還會重複掛兩份）。
- 新 skill 跟現有三份一樣：YAML `name` + `description`（含 WHAT／WHEN），流程連到本樹 `V21/` 或 `V19/`，不要把 `rules/*.md` 全文貼進 SKILL。
- 開機台請開 `<name>.code-workspace`（加入專案時會建立，內含 `../openness-standard`）。只 Open Folder 機台資料夾時，這些 skill **不會**出現。

加入專案步驟見各樹 `templates/add_project_onboarding.md`。
