# 這台電腦第一次打開本 repo

Cursor **不會**在 `git clone` 當下跳視窗。會在 **Open Folder**（或第一次對話）請你確認一次。確認紀錄只存在這台的 `.local/clone-confirmed`，不進 Git。

請確認：

1. 工作根目錄是 **`openness-standard`**，不是 `tia-openness-cursor-new` 或舊包 `tia-openness-cursor`。
2. Basic HMI 連線與 Softkey／Discrete 解法已掛在 `.cursor/rules/tia-hmi-autoload.mdc`。全文：[OPENNESS_NOTES.md](OPENNESS_NOTES.md) §10–§12。點法：[HOW-TO-DISCRETE.md](HOW-TO-DISCRETE.md)。
3. Cursor **Yes to All** 在這台 **Settings → Agents → Approvals & Execution**。clone 帶不走。
4. TIA Openness **AllowList** 也是這台；重編 exe 還會再問。
5. 只改工作副本。加入專案／寫新機先問 2～3 個參考路徑（可不給，改用 Practice）；用到的摘錄放機台 `reference/`。
6. 機台 repo 與本包**同層** clone。開機台請開 `<name>.code-workspace`（含本包），全隊 skill 在 [`.cursor/skills/`](../../.cursor/skills/README.md) 才會自動掛上。不要把 skill copy 進機台，也不要只放 `~/.cursor/skills/`。

看完：Open Folder 視窗按「是」，或在對話回 **`我看過了`**（英文可回 `I have read this`）。
