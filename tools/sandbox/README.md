# Windows Sandbox 驗證

需要系統管理員、或會改動系統的功能，用 Windows Sandbox 驗證：每次都是乾淨的 Windows，關掉就全部消失，不會動到自己的電腦。Sandbox 帳號是系統管理員、UAC 關閉，所以測試裡不會跳出權限詢問。

## 需求

- Windows 10／11 專業版或企業版，已開啟「Windows 沙箱」功能（`wsb` 指令可用）。
- .NET 10 SDK（測試前會先發佈一份自帶執行環境的程式）。
- 同一時間只能開一個 Sandbox。

## 執行

```powershell
pwsh -File tools\sandbox\Run-SandboxTest.ps1 -Test system-changes
```

結果寫在 `artifacts\sandbox\out\log.txt`，每一項檢查以 ✔／✘ 開頭；截圖在 `artifacts\sandbox\out\shots`。上一次的結果會改名成 `out-日期-時間` 保留。程式已經發佈過時可以加 `-SkipPublish`。

| 測試 | 檢查內容 | 約需時間 |
| --- | --- | --- |
| `network-traffic` | 網路頁「程式流量」：下載 57.3 MB 的檔案要記在 PowerShell 名下；ETW 工作階段只在畫面開著時存在 | 1 分鐘（需要網路） |
| `packet-capture` | 「擷取網路封包」：確認、開始擷取、提早結束仍存成 pcapng、刪掉 .etl；已有擷取在進行時說明原因 | 1 分鐘 |
| `system-changes` | 舊版 Windows 交給磁碟清理移除（不會經由連結刪到 C:\Users）、傳遞最佳化快取、清除卡住的列印工作、時間差 10 分鐘時網路診斷能發現並由「校正電腦時間」修正 | 3～5 分鐘（需要網路） |

| `health-report` | 系統管理員才讀得到的磁碟加密、開機時間，一鍵維護的系統穩定度與安全性設定，命令列匯出健檢報告並用 Edge 轉成圖片 | 1 分鐘 |
| `installer-upgrade` | 靜默安裝舊版（公司設定檔＋自動清理）→ 靜默升級新版 → 設定與自動清理保留、說明書與設定範例更新 → 命令列匯出 → 靜默解除安裝。要加 `-Installers <資料夾>`，放新舊兩個完整版安裝檔 | 2 分鐘 |

另外要加上 Sandbox 開機與登入的 1～3 分鐘。視窗出現「與 Windows 沙箱環境的連線已中斷」時，工具會自動按「重新連線」。

## 注意事項

- 測試腳本在 Sandbox 裡由 Windows PowerShell 5.1 執行，一定要存成 **UTF-8 含 BOM**，否則中文字串會變成亂碼、比對全部失敗。
- Sandbox 第一次開機有時會停在登入畫面：腳本等 3 分鐘沒登入就關掉重開，最多三次。電腦有人使用時比較容易登入成功。
- `.wsb` 已關閉 vGPU：開著時常出現「與 Windows 沙箱環境的連線已中斷」，登入指令也不會執行。
- 不要在本機用 `tail -f` 之類會一直開著 `log.txt` 的方式看進度，Sandbox 裡的寫入會失敗。
- 截圖只拍程式自己的視窗（PrintWindow），不擷取本機螢幕。使用說明書的網路畫面也在 Sandbox 拍，才不會拍到自己電腦的區域網路位址與 MAC。
- MessageBox 的按鈕在 UI Automation 只是 Pane，測試以控制項編號 1（確定）直接點；系統工具每列「執行」按鈕的名稱是該工具的標題。
