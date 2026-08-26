# Daily chat sync 政策（範本）

複製到 `<project>/plan/daily_chat_sync_policy.md` 後可改本專案差異。

觸發：使用者說收工備份、sync session、回家 push 等。見 `.cursor/rules/session-sync.mdc`。

1. 寫 `plan/session_YYYY-MM-DD_<tag>.md`（只用 [session_template.md](session_template.md)）
2. 更新主計劃裡本 session 定案的章節
3. 不要 add：`.ap21`、`.zap21`（除非本專案明文）、`Siemens.Engineering*`、`.env`
4. commit 訊息用一句話說明 session 主題
5. push 到 `origin`
6. 回覆 Resume prompt：下一 chat 要 `@` 哪些檔
