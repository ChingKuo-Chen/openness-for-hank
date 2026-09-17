# 寫／改機台專案

工具在各專案的 `host/` 或團隊的 Openness 工具倉庫。工作檔是副本，**參考只讀**。

## 開工前：2～3 個參考程式

Agent **主動建議**使用者提供 2～3 個參考程式，例如：

1. 最像這台的整線／上一台
2. 缺的站（給線、捲取、預扭…）從哪台抄功能
3. 另一條產線或舊版（對 IO／畫面）

沒有第 2、第 3 份就問「還有沒有可以對的」。不要默默只用一份。

路徑寫完整。HANDOFF 表格要列出這 2～3 個。見 [templates/HANDOFF.md.example](../templates/HANDOFF.md.example)。

參考可以對照、可以學寫法。**不要 Openness attach 參考。** 只開工作副本。

**讀完參考後：** 每個參考寫一則 [doc/notes/REF-*.md](../doc/notes/)（大綱＋心得）。失敗時先翻 notes／[OPENNESS_NOTES](../doc/OPENNESS_NOTES.md)，不要一開始就整份重讀。見 [read-and-note.md](read-and-note.md)。

## 開工順序

1. 確認 2～3 個參考、IO 準則、電路圖、工作副本路徑。
2. `robocopy` 其中一個適合當底的參考整包到桌面，`.ap21` 改名。不要用 Openness「另存」參考。
3. TIA **只開副本**。`--project:<完整.ap21>`。沒開就自己啟動 Portal V21。一個 GUI 一個專案。
4. 列出裝置、匯出（未編譯區塊會失敗，先記著）。
5. 命名：`--rename-text:舊=新`。軟體名唯讀，改 Device／CPU 名稱。
6. IO 對 C 表建 Hardware tag。衝突列 PROBLEMS，先不要改 LAD。
7. 缺的 PLC 從參考用 MasterCopy／無介面開源專案拷進副本，再接網路。
8. 隨時存檔。對話快滿先更新 HANDOFF。

## TIA / Openness

- Attach 參考會出 uncritical problem，不要做。
- 選專案比對**完整 `.ap21` 路徑**。
- Tag：一次給名稱、型別、位址。
- `--compile` 不會存，編譯完再存。
- 硬體中斷自己 GUI 綁 Channel。見 [tia-standing-orders.md](tia-standing-orders.md)。

## IO Excel

矩陣表：位址欄 + 右欄說明。正規化 `%I0.0`。
比對前拿掉 `LINE`／`全線`／`PBL`／`OP1`。≥80% → 同一點，**Tag 名留程式底**，址改 C 表。

三個來源都要比，取最高分：

1. Tag 名稱
2. TIA 註解
3. 舊 IO 表同一點的說明（來自參考程式）

文字對不到：先把舊名放到 C 表新位址。註解改 C。不必先問。

C 表多出來、舊 Tag 沒有的點：先不自動建新名，列 PROBLEMS。OP1/OP2 同一功能兩個位址都留。

## HMI OP1 / OP2

同一套畫面與 Tag／連線，只差 IP。不要把 HMI Tag 改綁 OP2 的 PLC 硬體點。件號先查目錄。

## 網路（S7-1200 I-device）

接到 Main 的 PN IO 系統：介面加上 IoDevice，再 ConnectToIoSystem。IP 避開已用的。

位址衝突先看 **Overview of addresses**。

I-device CD 對應區：**進 I-device 那台改**（X1 → Operating mode → I-device communication）。Openness `TransferAreas` 是空的，不要再挖。本機址有搬家選 Rewire tags。

## 硬體從零建

沒有可複製的裝置時，先 dump 再依計畫建。GSD／模組／HMI 先查目錄。

ET 200SP 實物 `6ES7 155-6AA02-0BN0` 是組合包；機架 IM 用 **AU02 / V6.1**，不要掛 AA02。

## 改現有程式

1. 先讀該機 HANDOFF。
2. 指定 PLC。匯出要改的塊。
3. Hardware Tag 名不改。
4. 同址對不上先討論。
5. LAD／SCL 維持來源語言。匯入用絕對路徑。編譯 0 錯再存。
6. 禁止 `PlcSoftware.Name =`。不要刪程式還在用、C 表沒寫的舊 Tag。
