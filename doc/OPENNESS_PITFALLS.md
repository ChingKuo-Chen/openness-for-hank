# TIA Portal V21 Openness — 踩雷筆記

從舊包 `HANDOFF-TIA-Openness.md` 遷入本標準 repo。  
目的：標出「已經證實走不通的路」，避免重走。手冊總覽 → [OPENNESS.md](OPENNESS.md)。

驗證環境：TIA Portal V21 Update 1、S7-1200 CPU 1212C DC/DC/DC、KTP1200 Basic PN、Windows 10 x64。
未在其他版本驗證過；V17–V20 的做法有相當比例在 V21 不成立。

---

## 一、開工前的一次性設定

### 1.1 帳號

Windows 帳號要加入本機群組 `Siemens TIA Openness`，加完必須登出再登入才生效。

### 1.2 組件：V21 已經拆開，沒有單一 `Siemens.Engineering.dll`

實際存在且要引用的四個：

```text
C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\
    Siemens.Engineering.Base.dll             基礎
    Siemens.Engineering.Step7.dll            PLC 側
    Siemens.Engineering.WinCC.dll            HMI 側
    Siemens.Engineering.WinCC.Extension.dll  HMI 側
```

編譯用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，一定要 `/platform:x64`。

執行期還要掛 `AssemblyResolve`，把 `Siemens.Engineering.*` 導到上面那個資料夾，否則載入 Base 之後找不到其餘組件。

**不要引用 `Siemens.Engineering.HW.dll` 或 `Siemens.Engineering.SW.dll`。**
這是 V17–V20 的寫法，V21 安裝目錄裡沒有這兩個檔。編譯會在檔案檢查就停住，而舊的 exe 不會被覆蓋 —— 接著你執行它，畫面顯示「連線成功」，但其實跑的是上一版程式，什麼都沒改到。這個假象很容易讓人往錯的方向查。

### 1.3 防火牆 AllowList：這是最大的時間黑洞

TIA 用「完整路徑 + 檔案修改時間 + SHA-256」認一支 Openness 程式。**每次重新編譯 exe 雜湊就變了，TIA 會再跳一次確認視窗。** 開發過程中一天可能跳幾十次。

第一次跳出來時按 **Yes to all**，不要按 **Yes**（`Yes` 只允許這一次）。

根本解法是讓編譯腳本自己寫 AllowList。機碼結構：

```text
HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList\<exe 檔名>\Entry (Build)
    Path          = exe 完整路徑
    DateModified  = LastWriteTimeUtc，格式 yyyy/MM/dd HH:mm:ss.fff（InvariantCulture）
    FileHash      = SHA-256 的 Base64
```

寫 HKLM 需要權限。做法是**一次性**用系統管理員把這一個機碼的寫入權授給工程帳號，之後每次編譯都不必提權。

提權時有個陷阱：提權後的 PowerShell 可能是 32 位元 host，`HKLM:\SOFTWARE\...` 會被靜默導向 `WOW6432Node`，於是明明機碼存在卻回報 `ItemNotFoundException`。用 .NET 的 `RegistryKey` API 搭配 `RegistryView.Registry64` 明確指定，並確認呼叫的是 `System32` 下的 `powershell.exe` 而不是 `SysWOW64`。

---

## 二、三條心法

這三條是整個專案最值錢的部分。技術細節會過時，這三條不會。

### 心法零：開工第一件事，先把整個專案匯出成範本庫

```cmd
TiaOpennessCheck.exe --project:"D:\path\Xxx.ap21" --list-devices
TiaOpennessCheck.exe --project:"D:\path\Xxx.ap21" --compile-all
TiaOpennessCheck.exe --project:"D:\path\Xxx.ap21" --export-all
```

**`--compile-all` 不能跳過。** 未編譯的區塊 `Export` 會直接丟 `EngineeringTargetInvocationException`。
實測一個 7 PLC、723 區塊的專案：升級後直接匯出是 **649 成功／145 失敗**；先編譯再匯出是 **794 成功／0 失敗**。

匯出結果在 `HmiExport\Templates\<專案名>\`，含所有 PLC 區塊、變數表、UDT、HMI 畫面、變數表、連線，外加 `INDEX.md`。

之後要建任何東西，先去這個資料夾找同類物件照抄，不要憑記憶寫 XML。下面兩條心法都是這條的延伸。

**舊版專案**：V19 以前用的是舊的單一 `Siemens.Engineering.dll`，V21 的工具打不到。
最省事的做法是把專案複製一份，用 TIA V21 開那個副本讓它自動升級（會另外產生 `<名稱>_V21` 專案，原始檔不動），再照上面三步跑。
實測一個 V19 專案升級後 7 個 PLC 全部 0 錯誤，可行。

### 心法一：不要猜 API，先匯出一份現成的當範本

V21 的屬性名稱、型別名稱跟舊版文件對不上。用「試 `SetAttribute` 看哪個名字會過」的方式推進，會浪費非常多時間 —— 本專案在 HMI Tag 綁定上試過 4 種屬性組合 × 2 種字串格式，全部失敗。

可靠的流程永遠是：

1. 在 TIA GUI 裡**手動做出一個**正確的東西（一個 HMI Tag、一個 I/O 域、一個 LAD 網路）
2. 用 Openness `Export` 成 XML
3. 讀 XML，看真正的結構長怎樣
4. 照著複製／改寫 XML，再 `Import(..., ImportOptions.Override)`

本專案的轉折點就在這裡：使用者手動建了一個 `test_db.Tag1`，之後 10 個 HMI Tag 全部是從那份 XML 複製出來的，一次就成。

如果卡住了，與其繼續猜，不如直接請使用者在 GUI 手動做一個範本給你。這通常比你自己試快十倍。

### 心法二：Openness 優先，GUI 自動化是最後手段

GUI 座標點擊很脆，而且更糟的是它會產生「看起來對、其實錯」的結果（見坑 4）。但確實有些事 Openness 做不到，見第三節。

### 心法三：改完一定要匯出交叉驗證

**畫面上顯示正常，不代表綁對變數。**

本專案最後抓到的錯誤就是這種：HOME PAGE 上十個欄位，標籤 Real1–Real10，數值格式都對，排版整齊 —— 但其中兩格綁到同一個變數。純看畫面永遠看不出來。

每次改完，用匯出的 XML 逐項核對，不要相信截圖。

---

## 三、能做什麼 / 不能做什麼

| 工作 | Openness | 備註 |
| --- | --- | --- |
| PLC Tag | 可以 | `PlcTagTable` + `Tags.Create(名稱, 型別, 位址)`，三個參數要一次給 |
| 全域 DB | **不能直接 Create** | 只能走 SCL External Source 或 XML Import |
| SCL / STL 區塊 | 可以 | `CreateFromFile(name, path)` + `GenerateBlocksFromSource` |
| LAD / FBD 區塊 | **只能 XML Import** | External Source 不吃 LAD |
| 編譯 | 可以 | `plc.GetService<ICompilable>().Compile()` |
| 匯出區塊 / 畫面 | 可以 | 診斷跟驗證都靠它 |
| HMI Tag | 可以（要範本） | Export 一個 → 改 XML → Import |
| HMI 畫面物件 | 可以（要範本） | `Screen.Export` → 改 XML → `Screens.Import` |
| HMI 連線 | 可以（要範本） | `hmi.Connections.Import`，但屬性要給齊，見下方 |
| DB 成員改名 | 建議用 GUI | 見坑 4，GUI 有 GUI 的價值 |

**HMI 連線**：`management.Connections.Create<HwHmiConnection>(...)` 走硬體那條路是失敗的，不要試。
但 `hmi.Connections.Import(...)` **可以**，實測建得出完整可用的連線。之前失敗是因為我只給了 `<Name>`，少了驅動和位址。真正需要的最小結構：

```xml
<Hmi.Communication.Connection ID="0">
  <AttributeList>
    <Driver>SMART_S7_1200_OMS</Driver>     <!-- S7-1200 用這個 -->
    <InterfaceType>ETHERNET</InterfaceType>
    <Name>Connection_1</Name>
    <Online>true</Online>
    <PhysicId>S7_ETHERNET_IP4</PhysicId>
    <ProtocolId>S7_ETHERNET_IP4</ProtocolId>
  </AttributeList>
  <ObjectList>
    <!-- IP 不在 AttributeList，在這裡 -->
    <Hmi.Communication.NameValuePair CompositionName="PhysicValues">
      <AttributeList><Name>LocAddress</Name><Value>192.168.0.2</Value></AttributeList>   <!-- HMI 自己 -->
    </Hmi.Communication.NameValuePair>
    <Hmi.Communication.NameValuePair CompositionName="ProtocolValues">
      <AttributeList><Name>RemStAddress</Name><Value>192.168.0.1</Value></AttributeList> <!-- PLC -->
    </Hmi.Communication.NameValuePair>
  </ObjectList>
</Hmi.Communication.Connection>
```

`Driver` 的字串不要猜，從既有專案匯出來看。另外 `connection.GetAttribute("Driver")` 讀不到值（會丟例外），要確認結果只能再 Export 一次來看。

早期我還有一個結論也是錯的：「V21 Openness 無法把 I/O 欄位匯進 KTP1200 Basic 的畫面」。正確做法是先匯入一個空白畫面、Export 出來，再照那份 XML 的結構填物件後 Import。直接手寫整份畫面 XML 才會失敗。

這兩個更正說明同一件事：**「Openness 做不到」的結論，十次有九次其實是「我給的 XML 不完整」。** 下結論之前先去弄一份真的匯出檔來看。

---

## 四、已驗證的配方

### 4.1 選對專案（每次都要做）

同時開兩個以上 TIA 時，`TiaPortal.GetProcesses()[0]` 常常不是你要的那個；只比對 `project.Name` 也不夠，不同資料夾可以有同名專案。

```csharp
project.Name == "test openess"
&& project.Path.FullName.Equals(
       @"C:\Users\Hank\Desktop\test openess\test openess.ap21",
       StringComparison.OrdinalIgnoreCase)
```

先跑一個 `--list-projects` 之類的唯讀指令拿到 Openness 回報的完整 `.ap21` 路徑，再寫死。TIA 標題列顯示的是資料夾路徑，跟 `Project.Path` 不同，別拿標題列的字去比對。

### 4.2 全域 DB

寫一份 `.scl`，用 External Source 產生。V21 匯入 DB 的 XML 時會檢查 `Namespace`，缺了會報 `Missing 'Namespace' identifier attribute`；`AttributeList` 至少要有 `Name` / `Namespace` / `Number` / `ProgrammingLanguage`。

區塊和 Tag 名稱**不能以數字開頭**。使用者說「建一個 123」，實際要建 `DB123`。

### 4.3 LAD 區塊

只能 XML Import。schema 版本不要用猜的，直接看本機檔名：

```text
C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\Schemas\
    SW.PlcBlocks.LADFBD_v5.xsd   ->  FlgNet/v5
    SW.PlcBlocks.SCL_v4.xsd      ->  StructuredText/v4
    SW.InterfaceSections_v5.xsd  ->  Interface/v5
```

`Parts` 放 `Access`（運算元）和 `Part`（指令），`Wires` 用 `Powerrail` / `IdentCon` / `NameCon` 接線。`IdentCon` 指向 `Access` 的 UId，`NameCon` 指向 `Part` 的接腳名。

接腳名：`Contact` / `Coil` 用 `in` `out` `operand`；`PBox` 用 `in` `bit` `out`；運算方塊用 `en` `in1` `in2` `out` `eno`；`Move` 用 `en` `in` `out1`；比較器的 power flow 輸入叫 `pre` 不是 `in`。

`TemplateValue` 每個指令要求不同，這部分沒有官方文件。以下是從一個真實專案的 **330 個 LAD 區塊**統計出來的實際用法（不是猜的）：

| 指令 | TemplateValue |
| --- | --- |
| `Contact` `Coil` `RCoil` `SCoil` `PContact` `NContact` `PCoil` | 無 |
| `PBox` `NBox` `Not` `Sr` | 無 |
| `Move` | `Card` |
| `O`（OR 方塊） | `Card` |
| `Add` `Mul` | `Card`，也可以再加 `SrcType` |
| `Sub` `Div` | 無，或只給 `SrcType`。**給 `Card` 會被擋** |
| `Calc` | `Card` + `SrcType` |
| `Gt` `Lt` `Ge` `Le` `Eq` `Ne` `InRange` `OutRange` `Abs` `Neg` | `SrcType` |
| `TON` `TOF` `CoilTON` | `time_type` |
| `LIMIT` | `value_type` |
| `MIN` `MAX` | `card` + `value_type`（注意是**小寫 `card`**） |
| `Scale_X` `Normalize` `Convert` `Round` | `DestType` + `SrcType` |
| `Inc` | `DestType` |
| `RD_LOC_T` | `date_type` |
| 程式庫 FB（`MC_*` `MB_*` `Modbus_*` `CTRL_HSC`） | 無 |

`MIN` / `MAX` 用小寫 `card`，而 `Add` / `Move` 用大寫 `Card` —— 這種事沒有範本絕對猜不到。

完整統計在 `HmiExport\Templates\<專案>\LAD-TemplateValue-usage.txt`，用 `MineLad.ps1` 針對任何匯出的範本庫重新產生。

**如果手上沒有範本庫**：先寫一個丟棄用的探測區塊，把要用到的指令各放一個試通，再寫正式區塊。匯入的錯誤訊息會指出哪個 CompileUnit、哪個 Part UId、哪個 TemplateValue 有問題。但這比直接翻範本慢非常多，能弄到範本就別試錯。

### 4.4 HMI Tag

**先分 Classic（`HmiTarget`）與 Unified（`HmiSoftware`）。** Unified 才有文件上的 `PlcTag` 屬性 R/W。細節 → [OPENNESS_NOTES.md](OPENNESS_NOTES.md)。

**V21 WinCC Basic（`HmiTarget`）：**

| 路徑 | 結果 |
|------|------|
| `ControllerTag` Import | **失敗**（`set_ControllerTag is not supported`） |
| 整表 Internal（拿掉 Connection） | 沒連 PLC — **拒收** |
| **Absolute** + `%DB…/%M…` + `Connection`，無 ControllerTag | **成功**（re-export 驗證） |

怎麼做 → [OPENNESS_NOTES.md §10](OPENNESS_NOTES.md)；寫專案注意（IO／Override）→ `host/Practice/io-merge/hmi-rewire/tf-live/OPENNESS-BASIC-HMI-TAG-HANDOFF.md`。

**仍可用的非綁定操作：** 列舉／刪 Tag、畫面綁「已有 HMI Tag 名」、Export 備份。不要用 `SetAttribute("PlcTag", ...)` 當 Classic 主路徑。

### 4.5 HMI 畫面

同樣是 Export → 改 → Import。改綁定就是改這一段：

```xml
<Hmi.Screen.Property ID="41" CompositionName="Properties">
  <AttributeList><Name>ProcessValue</Name></AttributeList>
  <ObjectList>
    <Hmi.Dynamic.TagConnectionDynamic ID="42" CompositionName="Dynamic">
      <LinkList>
        <Tag TargetID="@OpenLink"><Name>DB123_Real8</Name></Tag>
      </LinkList>
    </Hmi.Dynamic.TagConnectionDynamic>
  </ObjectList>
</Hmi.Screen.Property>
```

Import 前先備份匯出檔。實測 export → import 的來回是無損的（版面、字型、Bar、Layer 都保留），但還是備份比較安全。

Real 值要顯示成 `00.00`：`FormatPattern` 給 `99.99`，加上 `ShowLeadingZeros=true`。

---

## 五、GUI 自動化（真的躲不掉時）

用 PowerShell + `user32.dll` P/Invoke。以下是踩過才知道的：

**座標要量，不要目測。** 截圖之後裁切放大、畫上座標格線，再算點擊位置。直接看縮圖目測必錯。我用的截圖是 `CopyFromScreen(-8, -8)` 抓 1936×1176（Win10 最大化視窗左右各有 8px 邊框）。

**`SetForegroundWindow` 常常無效。** 要搭配 `AttachThreadInput` 把自己的執行緒接到目前前景視窗的執行緒上，才叫得動。

**輸入文字用剪貼簿，不要用 SendKeys 打字。** 儲存格編輯的固定套路：
`Click` → `{F2}` → `^a` → `^v` → `{ENTER}`。SendKeys 直接送中文或特殊字元會出錯。

**每一步都截圖確認**，不要連續送多個動作再一次檢查。TIA 反應慢，動作會掉。

**匯入畫面 XML 會把該畫面的編輯分頁關掉**，之後要從專案樹重新雙擊打開。專案樹太長時，選中節點按 `{LEFT}` 收合比找收合三角形的座標可靠。

---

## 六、坑（依重要性排序）

### 1. 重新編譯就會再跳防火牆 → 見 1.3，先解決這個再開工

### 2. 引用錯組件會編譯失敗、但舊 exe 還在 → 你會拿到假的成功訊息

編譯腳本要在失敗時明確停住，並且執行後確認 exe 的時間戳真的變了。

### 3. 猜 API 屬性名稱 → 換成「請使用者手動建一個範本」

### 4. 在 GUI 直接改名，改到的可能是別人 —— 本專案最嚴重的一個

在 DB 編輯器**最後一列打新變數名，等於把那一列原本的成員改名，不是新增**。

`DB123.Real10` 就是這樣被吃掉的。更麻煩的是 TIA 很忠實地把「改名」連鎖到所有參照：HMI 的 `DB123_Real10` 跟著指到新名字，HOME PAGE 上標著 Real8 的欄位也跟著綁到別的變數。結果是畫面完全正常、編譯 0 錯誤，但兩格顯示的是錯的值。這種錯誤只有交叉比對匯出檔才抓得到。

要新增成員請用「加入列」。

反過來說，這個連鎖行為在**真的要改名時是優點**：把 `pROGRESS` 改成 `Progress`，TIA 自動更新了 LAD 裡 8 處參照和 HMI Tag，一個都不用手動碰。所以改名走 GUI，新增走 API 或「加入列」。

### 5. 沒編譯就 Export，一定失敗

只要區塊處於未編譯／不一致狀態，`block.Export()` 就丟 `EngineeringTargetInvocationException`。
在 GUI 改過 DB 沒編譯會這樣，**剛從舊版升級上來的專案整包都是這樣**。

先 `--compile-all` 再匯出，不要一個一個去查為什麼失敗。

順帶一提：`--list-devices` 顯示的 PLC 數量如果是 0，但畫面上明明有 PLC，通常是裝置放在群組裡而列舉程式只掃了頂層。列舉裝置要同時涵蓋 `project.Devices`、`project.UngroupedDevicesGroup.Devices` 和 `project.DeviceGroups`（遞迴）。

### 6. 別在錯的專案上跑「無參數」模式

工具不帶參數時會刪掉並重建 Modbus 區塊。只想加 Tag 或加 DB 時務必帶對應參數。

### 7. 編譯訊息亂碼不代表失敗

中文訊息在 `cmd.exe` 會變亂碼。看結束代碼，不要看字。

### 8. `MB_CLIENT` 有兩套參數（只在做 Modbus 時相關）

CPU 1212C 用的是 V4+，需要 `CONNECT` 指向 `TCON_IP_v4`。`CONNECT_ID` / `IP_ADDR` / `IP_PORT` / `DATA_LEN` 都是舊版參數，V4+ 無效（`DATA_LEN` 的正確名稱是 `MB_DATA_LEN`）。
`RemoteAddress` 要寫 `(ADDR := [192,168,40,1])`，直接寫 `[192,168,40,1]` 會匯入成全 0 —— 看起來像「沒設 IP」。
`MB_DATA_PTR` 必須指向**非最佳化**的全域 DB。

### 9. 長跑 host「還在」≠「有在做事」——Agent 要自己查卡住

`TiaOpennessCheck` 進程活著、但 log 與 CPU 不動，就是卡住。  
**不要等使用者問「你有在跑嗎」。** 細節與處置 → [OPENNESS_NOTES.md §9](OPENNESS_NOTES.md)。

常見假活：開場 `FindAllPlcSoftwares` 掃大專案、Openness 防火牆對話框沒按、Import 後去碰已 disposed 物件把 session 弄死。

### 10. Basic HMI：不要批次 Export GraphicList；Softkey 不要走 composition.Import

`GraphicList.Export` 批次在 Basic 常炸，會 **dispose `HmiTarget`**，後面全掛。  
Global Softkey 的匯入是 **`HmiTarget.ImportScreenGlobalElements`**，不是 `ScreenGlobalElements.Import`。  
Discrete 沒有 Openness；xlsx zip 必須用 `/` 路徑，否則 `0032:000011` 且 log 空白。  
細節 → [OPENNESS_NOTES.md §12](OPENNESS_NOTES.md)、[HOW-TO-DISCRETE.md](HOW-TO-DISCRETE.md)。

---

## 七、收工前檢查清單

```powershell
# 1. PLC 區塊與 DB 成員是否完整
.\TiaOpennessCheck.exe --export-block:DB123
.\TiaOpennessCheck.exe --list-blocks

# 2. 畫面上每個物件綁哪個 HMI Tag
.\TiaOpennessCheck.exe "--export-screen:HOME PAGE"

# 3. 編譯
.\TiaOpennessCheck.exe --compile
```

換專案時每個指令都加 `--project:"完整路徑.ap21"`。不加就是打預設寫死的那個專案，這是刻意的保護。

HMI Tag 對應到哪個 PLC 變數，看 HMI 變數表的「PLC 變量」欄（Openness 這邊沒有現成指令）。
HMI 要另外在 GUI 編譯（選 HMI 裝置按 Ctrl+B），PLC 的 `--compile` 不含 HMI。

最後確認狀態列有「專案已成功保存」。

---

更細的錯誤訊息原文與對照，見同專案的 `README.md`。
