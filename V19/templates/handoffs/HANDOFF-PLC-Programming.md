# 新對話用：TIA PLC 程式撰寫實戰摘要

更新：2026-09-08。這份是給另一個 Cursor 對話寫 PLC 程式時的起點；內容是這次實際寫入、匯入、編譯所得到的規則，不是只靠文件推測。

## 可直接貼到新對話的開場

```text
我要改 TIA Portal V21 的 PLC 程式。請先讀 AGENTS.md，再讀規定
tia-standing-orders.mdc、寫程式 tia-lad-scl-lessons.mdc / tia-ladder.mdc、
寫專案 tia-modify-program.mdc，以及目前機台 HANDOFF。

只改工作副本，不改任何參考專案。先用完整 .ap21 路徑確認目標專案，
再指定 --plc:<PLC名稱>。先匯出要修改的區塊，確認它原本是 LAD、SCL
或混合區塊；原本 LAD 維持 LAD，原本 SCL 維持 SCL。LAD 用 LadWriter
規格產生，SCL 用 .scl External Source。每次只匯入少量區塊，編譯 0
錯誤後再存檔，最後匯出 XML 驗證接線或 SCL 本文。不要猜 TIA API、
TemplateValue、指令埠名或硬體位址。
```

## 最重要的安全規則

1. 工作專案與參考專案必須分開。參考專案只能讀、匯出或用無介面
   `MasterCopy` 取資料；**絕不能用 Openness attach 參考專案**。
2. 一個 TIA GUI 只開一個專案。要改的專案沒開時，工具應自行啟動
   Portal V21 並開工作副本，不要要求使用者手動開。
3. 選專案要比對完整 `.ap21` 路徑，不能只比 `project.Name` 或視窗標題。
4. 指令一定帶 `--project:"完整路徑"` 和 `--plc:<名稱>`。專案有多台 PLC
   時，不指定 PLC 很容易改錯台。
5. 不帶參數執行工具可能走舊的重建流程；只做明確、窄範圍的指令。
6. 每次寫入後立即存檔。`--compile` 不會替專案存檔，編譯完一定再
   `--save`。

## 開工流程

1. 若這輪改過 `Program.cs` 或 `LadWriter.cs`，先執行：

```powershell
.\Build.ps1
```

`Build.ps1` 同時更新 TIA Openness AllowList；只編譯 C#、沒重新 Build 就
執行，可能仍在跑舊版 exe。

2. 連到工作副本，確認裝置與 PLC 名稱：

```powershell
$project = 'C:\完整路徑\工作專案.ap21'
.\TiaOpennessCheck.exe --project:$project --list-devices
```

3. 先編譯目標 PLC，再匯出要修改的區塊。舊版剛升級或資料型別不一致時，
未編譯的區塊常常無法匯出。

```powershell
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC --compile
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC `
  --export-block:區塊名 --to:'C:\絕對路徑\before.xml'
```

4. 先從匯出的 XML 判斷區塊語言及既有介面，再寫需求對應的修改。程式
功能必須來自需求與既有介面，不是把另一台機台的 XML 全份改名後硬塞。

## LAD：從需求寫新邏輯的標準做法

LAD 不走 External Source；必須用 XML 匯入。但**不要手寫 FlgNet、UId 或
Wire**，應寫精簡規格，再由 `LadWriter` 產出合法 SimaticML。

規格放在 `Practice\`，參考範本：
`HmiExport\Conveyor_Ctrl.lad.xml`。

```powershell
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC `
  --write-lad:'C:\絕對路徑\Foo.lad.xml'
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC `
  --import-block:'C:\絕對路徑\Foo.generated.xml'
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC --compile
.\TiaOpennessCheck.exe --project:$project --save
```

LAD 的核心經驗：

- `Series` 是串聯；`Parallel` 是並聯，且至少必須有兩條支路。
- `Fork` 用於同一電流來源分到多個目標；同一來源不能硬做出多條 Wire。
- 接點／線圈的電流腳位是 `in` → `out`。
- 比較方塊（`Gt`、`Lt`、`Eq` 等）電流輸入是 `pre`，不是 `en`。
- 運算方塊（`Add`、`Move`、`Calc` 等）電流輸入是 `en`，輸出是 `eno`。
- `Inc`／`Dec` 的資料腳位叫 `operand`。
- `TON`／`TOF`／`TP` 要用 `time="T#2s"`，不可把時間當普通常數。
- `Calc` 的方程式裡不要塞字面常數；把常數明確做成輸入。
- `Add`／`Mul`／`Move` 的 cardinality 通常是 `Card`；`MIN`／`MAX` 卻是
  小寫 `card`；`Sub`／`Div` 不可亂加 `Card`。
- 新指令的埠名、版本、TemplateValue 一律先查 `LadWriter.cs` 最上方的
  指令表或真實匯出檔；表裡沒有就先做一個丟棄用的 GUI 範本量測。

LAD 匯入後，除了編譯，也要再次匯出 XML 核對 `Parts`／`Wires`，確認 TIA
沒有把並聯、指令或資料接線降級。

## SCL：從來源檔產生區塊

原本是 SCL 的區塊，維持 SCL。來源放在 `Practice\Scl\`，用 External
Source 產生：

```powershell
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC `
  --import-scl:'C:\絕對路徑\Foo.scl'
.\TiaOpennessCheck.exe --project:$project --plc:目標PLC --compile
.\TiaOpennessCheck.exe --project:$project --save
```

- `--import-scl:` 會先刪除同名 `FUNCTION`／`FUNCTION_BLOCK` 再重新產生；
  所以被其他 LAD 呼叫的介面、區塊名稱與編號不能任意改。
- 批次匯入時一次少量。大量 SCL 匯入會耗盡 TIA GUI 資源；用
  `Import-Practice-Batch.ps1` 分批。
- SCL 避免保留字當參數名，例如 `IN`／`Out`；改用 `in_val` 之類名稱。
- `CASE 0, 1:` 在此環境不接受，應拆成 `0:` 與 `1:`。
- 型別運算要明確轉換。例如 `LReal + Real` 先做 `REAL_TO_LREAL`。
- 匯入成功不代表功能正確：要匯出或查看 SCL 本文，確認每個分支、保護條件
  與輸出都有保留。

## 修改既有「混合 LAD + SCL」區塊

有些 OB（例如本案的 Cyclic interrupt／OB30）同時含 LAD 與 SCL network。
這種區塊不可用單一 `.scl` 重建，否則會吃掉 LAD 部分。正確做法是：

1. 先編譯、匯出整個區塊。
2. 以 `XDocument` 只改需要的 CompileUnit，保留其他 LAD／SCL network。
3. 用 `ImportOptions.Override` 匯回原區塊。
4. 匯入後重新用名稱取得 `PlcBlock`；舊的 Openness object 可能已
   `Disposed`。
5. 編譯 0 錯誤後再次匯出，驗證 XML。

這次實測到的 XML 細節：

- 新增 SCL 運算因子時，`Access` 必須放進該指令的
  `NamelessParameter`，不能直接放在 `Instruction` 下。
- 新增 LAD Contact 不是只加 `Part` 和 `operand` Wire；還必須接
  `Powerrail` 的 `in`、Contact `out`，並維持 `Parts` 和 `Wires`
  的電流流向排序。否則 TIA 會報「elements must be sorted according to
  the current flow」。
- 新增資料引用前，DB／UDT 成員必須先存在。例如新增
  `opt.die_compression.24B` 後，OB30 才能引用它。
- 匯入 OB 後，需重新確認週期型 OB 的 `CyclicTime`；本案以
  `SetAttribute("CyclicTime", 10)` 恢復為 10 ms。
- XML `ID="18B"` 是十六進位物件 ID，不是站名。不可全檔
  `18B → 20B` 取代，只改 `Name`／顯示文字／Component 名稱等正確欄位。

## Tag、位址與資料區塊

- PLC Hardware Tag 用一次完整建立：
  `Tags.Create(名稱, 型別, 位址)`。不可分三次設定。
- Hardware Tag 名稱是程式介面。C 表／圖面位址調整時，原則上保留程式名，
  搬到正確新位址；不要因文字想一致就改掉程式正在使用的 Tag 名。
- 同位址或文字對不上時，先列為 PROBLEMS，再討論邏輯。不要靜默刪掉 C 表
  沒寫但程式仍在用的 Tag。
- 對新 C 表需同時比較 Tag 名、TIA Comment、舊 IO 表同點描述；文字分數
  達 55–80% 也可視為同功能。對不到的新點先建立 Hardware Tag，**不代表
  可以自動編程**。
- 全域 DB 不能靠普通 Openness `Create` 建成；用 SCL External Source 或
  匯出 XML 改完再 Import。
- 名稱不可用數字開頭。人說「123」時，區塊名要寫為 `DB123` 之類合法名稱。
- 硬體／圖面位址疑似衝突時，先跑
  `--overview-addresses --plc:<CPU名>`。I-device、RIO、Drive 的保留區
  不會只出現在 CPU 機架插槽上。

## HMI 與 PLC 的交界

- OP1／OP2 是同一套 PLC 邏輯，PLC 側應 OR 合併兩台實體按鈕；不要把
  HMI Tag 改綁 OP2 PLC 硬體點。
- HMI Tag／畫面／連線都先 Export 一個正確範本再改 XML Import。不要猜
  `ControllerTag` 或連線屬性。
- WinCC Basic 的 Discrete alarms 沒有可用的 Openness composition；
  用 Discrete alarms 編輯器匯出／匯入 xlsx。此案 HMI 不可任意 Compile，
  只有明確要求且先處理已知問題時才做。
- GUI 自動化是最後手段。必須先固定並聚焦 TIA 視窗、截圖確認座標、每一步
  後再截圖；文字用剪貼簿貼上，別用 SendKeys 打中文。

## 本案（25017 / 26037_Cusor）目前已完成，勿重做

工作副本：
`C:\Users\Hank\Desktop\26037_Cusor\26037_Cusor.ap21`

- 硬體、PF PLC、24B RIO／Drive、OP1／OP2 HMI 已建立。
- 四站定位已接 `I_Snr_*_Section4`，且 `section_no = 4`。
- 20B #19／#20 PLC events、OP1／OP2 HMI Discrete alarms 均完成；兩台
  HMI 各 304 筆。
- OB30 已把 24B 加進 1B 緊線 OR 與線速壓縮比鏈。
- `opt.die_compression.24B` 初值暫用 0.95，實機試車需確認。
- 最後 Main PLC 編譯 0 錯，專案已存檔。

本案尚未做、且使用者暫停的項目：

1. C 表新點的完整邏輯：PreTwist、定位銷／離合器第 2 組、Home、全線急停、
   塔燈、給線／收線與 UPS 等。這些多數沒有可直接複製的舊程式，需先拿到
   功能需求。
2. 四台 Inside PLC 的新 I/O 邏輯：S4 鐵軸／安全板／鎖定、編碼器、
   Line speed／run 到 servo、S4 解鎖與 Bobbin lock。Tag 已齊，程式未寫。
3. 實機試車要確認 24B 壓縮比與 I/O 動作。

本案明確不做：

- PF 不建獨立 Drive／HMI。
- Inside PLC 不接 Main 的 `PN/IE_1`；PLC 間僅走既有 RIO ZigBee 架構。
- 不要執行 `--fix-20-24-compile`、`--align-24b-drive`、完整
  `--sync-drawing-hw` 或 `--rename-text:18B=20B`。

## 完成定義與收工

一個 PLC 修改只有同時滿足以下條件才算完成：

1. 匯入的區塊、DB、Tag 都在正確 PLC／資料夾。
2. 目標 PLC `--compile` 為 **0 errors**。
3. 已再執行一次 `--save`。
4. 已匯出 XML 或 SCL 交叉確認關鍵介面、Parts／Wires、分支與 Tag 引用。
5. 已把「完成、未完成、已知限制、下一步」寫回對應 `HANDOFF-*.md`。

若 TIA 出現 `0024:000004` GUI 資源警告，先關區塊分頁並使用
`Dismiss-TiaDialog.ps1`；必要時重開**工作副本**的 TIA，再繼續。不用為了
繞過錯誤去打開或 attach 參考專案。
