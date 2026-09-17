# LAD 規格（LadWriter）

只寫精簡 XML 規格，讓 `host/LadWriter.cs` 產生可匯入的 FlgNet。  
**不要**手寫 `FlgNet`／`UId`／`Wire`。埠名從真實匯出或本檔指令表抄，**不准猜**。

成功流程與 SCL 對照 → [tia-write-program.md](tia-write-program.md)。  
已驗證範本庫 → [../Practice/](../Practice/)（說明 [../doc/LAD_PRACTICE.md](../doc/LAD_PRACTICE.md)）。

入門範本：`Practice/examples/Blink.lad.xml`、`Practice/examples/Conveyor_Ctrl.lad.xml`。

---

## 規格骨架

```xml
<Lad kind="FB" name="Foo" number="901" comment="區塊說明">
  <Interface>
    <Section name="Input"><Member name="start" type="Bool" /></Section>
    <Section name="Output"><Member name="run" type="Bool" /></Section>
    <Section name="Static"><Member name="t_ramp" type="IEC_TIMER" /></Section>
  </Interface>
  <Network title="start latch">
    <Series>
      <Parallel>
        <Contact var="start" />
        <Contact var="jog" />
      </Parallel>
      <Contact var="stop" negated="true" />
      <SCoil var="latch" />
    </Series>
  </Network>
</Lad>
```

葉節點：`Contact` `PContact` `NContact`、`Coil` `SCoil` `RCoil` `PCoil` `NCoil`、`Not` `Sr` `Rs`、`Box name="TON|Move|Gt|Add|Call|…"`。  
結構：`Series`（串）、`Parallel`（並，**至少兩支**）、`Fork`（一條電流分多路；**同一來源只能一條 Wire**）。

---

## 埠名（錯了 TIA 會降級或 Import 失敗）

| 類 | Power in → out | 注意 |
|----|----------------|------|
| Contact / Coil | `in` → `out` | 負極 `negated="true"` |
| 比較 Gt Lt Ge Le Eq Ne | `pre` → `out` | 不是 `en` |
| 運算 Add Sub Move Calc | `en` → `eno` | |
| TON TOF TP | `IN` → `Q` | `time="T#2s"`，不要 `const=` |
| Inc Dec | 埠 `operand` | Template `DestType` |
| Call | 呼叫其他 FB/FC | 腳位對齊被叫端 |

新指令：在 `LadWriter` 指令表**加一列**，不要改接線邏輯。版本號從匯出檔抄。

---

## 練習／可攜寫法（實測）

| 做 | 不做 |
|----|------|
| 廠端全域／TO 改成**腳位** | 綁死某一台的全域標籤 |
| inline Struct／Array | 無必要就拉 UDT |
| 語言跟來源（LAD 就 LAD） | 把 SCL 區塊改寫成 LAD（或相反） |
| 從需求寫規格 | 從參考機 XML 複製再改名 |

`PTO_Ctrl`／`SPD_PTO_Ctrl` 在無工藝物件的 CPU（如練習用 1215）**不要**加 `TO_Axis`／`MC_*`。不要依賴 `FirstScan` 全域標籤。

---

## 禁止

- 從 23019／24137／22021 等參考匯出複製 XML 再改當解法
- `Parallel` 只包一個分支
- `Calc` 方程式裡寫字面常數（常數當 IN）
- `Sub`／`Div` 給 `Card`；`MIN`／`MAX` 的 cardinality 是小寫 `card`
- `--import-block:` 用相對路徑（會被錯接；必須**絕對路徑**）
