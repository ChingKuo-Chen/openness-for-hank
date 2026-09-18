# Openness 環境設定

**把這份文件丟給 Cursor 即可。**

Cursor 會自己檢查這台電腦、能裝的自己裝。只有這三件事它做不到，會請你按一下：

1. 用「系統管理員」開 PowerShell（照它貼的指令跑）
2. **登出 Windows 再登入**
3. TIA 跳出防火牆時按 **Yes to all / 全部允許**（不要按 Yes）

做完就能用本樹 `host/` 的工具連上 TIA Portal V19。

請先確認：已裝 **TIA Portal V19**、已 clone **openness-standard 完整倉庫**。Cursor 跟 TIA 用同一個 Windows 帳號。本檔在 `openness-standard/V19/host/`。

安裝腳本與本檔放在同一層（`host/`），不要拆走：

- `INSTALL.md`
- `Build.ps1`
- `GrantAllowListAccess.ps1`

---

## 若 Cursor 請你開系統管理員 PowerShell

開始選單 → Windows PowerShell → 右鍵 → **以系統管理員身分執行** → UAC 按是。視窗標題要有「系統管理員」。

先貼這行，要出現 `True` 再繼續（出現 `False` 就關掉重開）：

```powershell
([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

接著照 Cursor 貼的路徑執行即可。若出現「錯誤 5／存取被拒」，代表這個視窗還不是系統管理員。

加完群組後一定要**登出再登入**，只關 TIA 不夠。

---

# Agent

讀完就做。先檢測，再補缺，最後才請人。安裝與編譯都在 **`host/`**（有 `Build.ps1` 的那層），不是倉庫根目錄。給使用者的指令用 PowerShell，路徑用目前工作區的 `host/`，不要寫死使用者名稱，不要 `cd /d`，不要自己彈 UAC。帳號還不在群組、或還沒登出再登入時，不要連 Portal。

- API：`C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\`  
  `Siemens.Engineering.dll`、`Siemens.Engineering.Hmi.dll`
- 編譯器：`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- Portal：`C:\Program Files\Siemens\Automation\Portal V19\Bin\Siemens.Automation.Portal.exe`

**檢測通過：** 兩顆 DLL 與 `csc.exe` 存在；群組 `Siemens TIA Openness` 存在且 `net localgroup`、`whoami /groups` 都有目前使用者；`host/` 有 `Build.ps1`、`GrantAllowListAccess.ps1`；有 `host/TiaOpennessCheck.exe`（沒有或原始碼較新就在 `host/` 跑 `Build.ps1`，結束代碼 0）；Build 已寫上 AllowList 或之後請管理員跑一次；TIA 在跑時 `.\TiaOpennessCheck.exe --list-projects` 結束代碼 0（沒開專案可印 `尚未開啟或讀不到`）。

**自己補：** `Set-Location` 到 `host/` 後，缺 exe 就 `powershell -ExecutionPolicy Bypass -File .\Build.ps1`。群組在、帳號不在，先試 `net localgroup "Siemens TIA Openness" "$env:USERDOMAIN\$env:USERNAME" /add`（錯誤 5 請使用者）。TIA 沒開就自己啟動 Portal，不要叫使用者開。

**請使用者：** 沒有 V19／沒有該群組 → 先裝 TIA。加群組或寫 AllowList 被拒 → 系統管理員 PowerShell，標題有「系統管理員」、提權檢查 `True`，然後 `Set-Location` 到 `host/`，跑 `net localgroup ... /add` 與 `GrantAllowListAccess.ps1`（log 要 `OK`；用 `System32` 的 PowerShell）。剛加入群組 → 登出再登入。防火牆 → **Yes to all**，不要 Yes。
