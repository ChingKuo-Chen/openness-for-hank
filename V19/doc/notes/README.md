# 參考程式／手冊心得索引

最後更新：2026-09-17。

**規矩：** [rules/read-and-note.md](../../rules/read-and-note.md)  
失敗時：**先讀這裡與 OPENNESS_NOTES／PITFALLS → 再翻官方手冊 → 最後才整份重讀原文。**

## 手冊／Openness 心得（全隊通用）

| 檔 | 主題 |
|----|------|
| [doc/OPENNESS_NOTES.md](../OPENNESS_NOTES.md) | Openness PDF／V21 實測（Absolute、stall、Recipe） |
| [doc/OPENNESS_PITFALLS.md](../OPENNESS_PITFALLS.md) | V21 踩雷 |
| [OPENNESS-BASIC-HMI-TAG-HANDOFF.md](OPENNESS-BASIC-HMI-TAG-HANDOFF.md) | 寫專案注意：參考補缺、對話交接、IO／Override、HMI 選路 |
| [LAB-TEST-V19-STANDARD.md](LAB-TEST-V19-STANDARD.md) | V19 實驗場結論（能做／做不到） |

## 解法（不要重試）

**自動載入（clone 後每則對話）：** `.cursor/rules/tia-hmi-autoload.mdc`、`tia-tools-autoload.mdc`。

| 檔 | 主題 |
|----|------|
| [OPENNESS_NOTES.md §12](../OPENNESS_NOTES.md) | Softkey／圖形清單／Discrete：怎麼修、哪條路不要再試 |
| [OPENNESS_PITFALLS.md §六.10](../OPENNESS_PITFALLS.md) | GraphicList 批次 Export、Softkey Import API、xlsx zip `/` |
| [HOW-TO-DISCRETE.md](../HOW-TO-DISCRETE.md) | Discrete 編輯器怎麼點、同一 Word 一個 bit |

## 參考程式大綱＋心得

讀某一台／某一塊參考程式後，在本目錄新增 `REF-<塊或主題>.md`（大綱＋心得）。  
**機台專用 REF 不必 push**；只有「大家寫機都會用到的 Openness／手冊結論」才進遠端。
