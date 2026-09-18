# 寫西門子 TIA／PLC — 規定總表

只適用 TIA Portal／S7 PLC／WinCC HMI／Openness／LAD／SCL。不是西門子的工作略過。

倉庫三塊：**規定（這份）**、**寫程式**（[tia-write-program.md](tia-write-program.md)）、**寫專案**（[tia-write-project.md](tia-write-project.md)）。這裡是不准破的。

## 參考程式（必做）

寫新機或加入專案時，Agent **先問 2～3 個參考 TIA 路徑**。

- 使用者**可以不給**。沒有參考 → 用本樹 `Practice/`，不要停工。
- 實際用到的塊／硬體／IO 放機台 `reference/`（不要整包 `.ap19`／`.ap21`）。
- 參考只讀。工作只改副本。
- **不要 Openness attach 參考專案**（會變成在改參考）。
- 可以對照參考的寫法、區塊、畫面；不限定某一個舊機號。
- **讀完每個參考 → 寫 `doc/notes/REF-*.md`（大綱＋心得）**，不要只留在聊天。見 [read-and-note.md](read-and-note.md)。

## 全隊 Cursor skill

- 全隊都用的 skill 只寫 **openness-standard 根目錄** `.cursor/skills/`（見該目錄 README）。
- **不要** copy 進機台 repo，**不要**只放自己的 `~/.cursor/skills/`。
- 開機台用 `<name>.code-workspace`（含 `../openness-standard`），skill 才會自動掛上。

## 失敗時先讀心得／手冊

卡住或編譯失敗時順序：

1. `doc/notes/`、`doc/OPENNESS_NOTES.md`、`doc/OPENNESS_PITFALLS.md`、該機 HANDOFF  
2. 官方 PDF／Siemens docs 對應章  
3. 仍不夠才整份重讀參考程式或手冊

## 練習（LadWriter）

- 來源是 LAD 就寫 LAD，是 SCL 就寫 SCL。不要互轉。DB 不是程式。工具用 C# / PowerShell。
- LAD 從需求寫，走 `LadWriter`。埠名不准猜。規格見 [tia-lad-spec.md](tia-lad-spec.md)；寫到 0 錯見 [tia-write-program.md](tia-write-program.md)。
- 已驗證範本庫在本 repo [`Practice/`](../Practice/)（說明 [doc/LAD_PRACTICE.md](../doc/LAD_PRACTICE.md)）。**不要**依賴某人本機舊路徑。
- `--import-block:` / `--import-scl:` **絕對路徑**。畫面看起來對不算；匯出對過或 SCL 對過，編譯 0 錯才算。
- 練習 FB 在練習 PLC 過了才灌機台 `Main`。寫機台時不要重匯練習區塊。
- 對話快滿先更新該機 `handoffs/HANDOFF-*.md`。

## 工作專案

- 只改工作副本。參考不要另存成工作檔。
- 專案沒開就自己啟動 Portal V21 開那個 `.ap21`，不要叫使用者開。一個 GUI 只開一個專案。`--project:` 比完整路徑。
- `robocopy` 整包再改名。PLC/HMI 軟體名唯讀，改 Device／CPU `SetAttribute("Name")`。
- 改完 host `.cs` 要編譯 host。隨時 `--save`。`--compile` 不會存。
- 沒叫不要建 GitHub org、不要對機台 repo 自己 `git init`／push。

## 區塊資料夾

- 新建／匯入的 FB、FC、DB **不准堆在 Program blocks 根目錄**。放進該 PLC 既有編號夾，不要自創夾名。
- **凡是 OB 都放外層**，跟 `Main`（OB1）同一層。不要把 OB 塞進編號夾（Openness 搬 OB 會掉硬體中斷 Channel）。
- `--import-scl` 生在根目錄，生完立刻把 FB／FC／DB 搬進對的夾。亂了用整理指令（OB 一律留根）。

## IO／Tag／程式

- Hardware Tag **名不改**（程式綁名）。C 表說明可寫 Comment。
- 對 C 表要比 **Tag 名、TIA 註解、舊 IO 表說明**，取最高分；≥80% 名留程式底、址改 C。對不到也先搬到 C 表新址，不要留舊址撞名。
- 同址功能對不上：名留舊的，列 PROBLEMS，先討論再改邏輯。C 表沒寫、程式還在用的舊 Tag 不要刪。
- OP1／OP2 同一功能兩個位址都留，程式 OR。不要自己開下一項 C 表新點。

## HMI

- Main 兩台：同一套畫面與連線 Tag，只差 IP。不要把 HMI Tag 改綁 OP2 PLC 硬體點。
- **不要 Compile HMI**（WinCC Basic 常爆一堆錯），除非使用者這輪明說要編譯。
- Discrete 沒有 Openness API，走編輯器 xlsx。OP1、OP2 同一份。同一 Trigger Word 一個 bit 只能一筆；xlsx zip 路徑用 `/`。
- Trigger bit：偶數 byte 的 `Mxxx.0` 要選 HMI **`.8`**，不是 IEC `.0`。
- Softkey 在 `ScreenGlobalElements`；匯入用 `HmiTarget.ImportScreenGlobalElements`。不要 `composition.Import`、不要逐畫面改 Softkey、不要批次 `GraphicList.Export`。
- 上列細節：[doc/OPENNESS_NOTES.md](../doc/OPENNESS_NOTES.md) §12、[doc/HOW-TO-DISCRETE.md](../doc/HOW-TO-DISCRETE.md)。
- 件號先查目錄，不要靠記憶。

## 硬體／網路

- 位址衝突先看該 CPU **Overview of addresses**（含 I-device／RIO／Drive）。
- S7-1200 I-device CD 對應區進 **I-device 那台**改。Openness `TransferAreas` 是空的，不要再挖。
- Inside **不接** Main `PN/IE_1`（這類拓撲以該機 HANDOFF 為準）。RIO IM 機架用 AU02 V6.1，不要掛 AA02。
- XML `ID="18B"` 是 hex，不要整份字串取代。
- **硬體中斷自己用 GUI 搭好並綁定**，不要留給使用者。Openness 設不到 Channel。中斷 OB 跟 `Main` 同層；綁好後不要再用 Openness 搬。

## 怎麼做事

- 對使用者用**繁體中文**（台灣用語）。
- 只有使用者明確說「記得」才寫進本 repo 規定並 commit + push。沒說「記得」不要當永久記憶、也不要為此自動 push。
- 建完必查：Tag／硬體／接線做完立刻跑檢查，回報「有 Tag 沒程式／缺址／編譯錯」。
- 做完列剩餘，對齊該機 HANDOFF「還沒做」。見 [list-remaining.md](list-remaining.md)。
- TIA 可以短暫置頂來點；點完立刻拿掉置頂。不要對整棵 TIA 做 UIA Descendants。
