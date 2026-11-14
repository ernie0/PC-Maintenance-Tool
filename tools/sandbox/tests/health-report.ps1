# 以系統管理員才讀得到的健康資訊與健檢報告：
# - 電腦資訊的「安全性」讀得到磁碟加密狀態（一般權限時只說要系統管理員）；
# - 開機與背景顯示開機時間或說明 Windows 還沒記錄；
# - 一鍵維護的「系統穩定度」「安全性設定」有結果；
# - 命令列 --export-report 寫出報告，用 Edge 轉成圖片檢查版面。
. (Join-Path $PSScriptRoot 'common.ps1')

try {
  Start-Sleep -Seconds 8
  Log "── 健康資訊與健檢報告（系統管理員：$(IsAdministrator)）"
  Start-App
  Log "  $((Texts | Where-Object { $_ -like '版本 *' }) -join '')"

  Select-Item '電腦資訊' $CT::ListItem
  $null = Wait-Until { Texts | Where-Object { $_ -eq '上次更新' } } 60
  Start-Sleep -Seconds 2
  $encryption = Texts | Where-Object { $_ -like '系統磁碟*加密*' -or $_ -like '*BitLocker*' -or $_ -like '以系統管理員重新啟動後*' } | Select-Object -First 1
  Check '系統管理員讀得到磁碟加密狀態' ([bool]$encryption -and $encryption -notlike '以系統管理員*') "$encryption"
  Log "  穩定度：$((Texts | Where-Object { $_ -like '沒有*' -or $_ -match '^\d+ 次' }) -join ' ／ ')"
  Shot 'computer-info'

  Select-Item '開機與背景' $CT::ListItem
  $boot = Wait-Until { Texts | Where-Object { $_ -like '上次開機到桌面*' -or $_ -like 'Windows 還沒有記錄開機時間*' -or $_ -like '無法讀取*開機*' -or $_ -like '以系統管理員執行可以看到*' } | Select-Object -First 1 } 60
  Check '開機時間讀得到（或說明還沒有紀錄）' ([bool]$boot -and $boot -notlike '以系統管理員執行*' -and $boot -notlike '無法讀取*') "$boot"
  Shot 'startup'

  Select-Item '一鍵維護' $CT::ListItem
  Press (Find-El '一鍵掃描' $CT::Button)
  $null = Wait-Until { Texts | Where-Object { $_ -eq '這台電腦狀態良好' -or $_ -like '發現 * 項可以一鍵處理*' -or $_ -like '有 * 項建議，請看下方說明' } } 300
  Start-Sleep -Seconds 2
  $stability = Texts | Where-Object { $_ -like '最近 30 天*' -or $_ -like '無法讀取 Windows 事件記錄' } | Select-Object -First 1
  $security = Texts | Where-Object { $_ -like '*安全性設定建議調整' -or $_ -eq '防火牆、使用者帳戶控制等設定正常' } | Select-Object -First 1
  Check '一鍵維護：系統穩定度有結果' ([bool]$stability) "$stability"
  Check '一鍵維護：安全性設定有結果' ([bool]$security) "$security"
  Shot 'maintenance'
  Stop-App

  $report = Join-Path $script:out 'report'
  New-Item -ItemType Directory -Force $report | Out-Null
  $p = Start-Process $script:exe -ArgumentList '--export-report', "$report\" -PassThru -Wait
  $file = Get-ChildItem $report -Filter '*.html' | Select-Object -First 1
  Check '命令列匯出健檢報告' ($p.ExitCode -eq 0 -and $file -and $file.Length -gt 3000) "結束代碼 $($p.ExitCode)，$($file.Name)，$($file.Length) bytes"
  if ($file) {
    $html = [IO.File]::ReadAllText($file.FullName)
    Check '報告讀得到磁碟加密與開機資料' ($html -notlike '*以系統管理員重新啟動後才能讀取*' -and $html -notlike '*以系統管理員執行才能讀取*')
    $edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($edge) {
      Start-Process $edge -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars', '--window-size=1000,2200', "--screenshot=$(Join-Path $script:shots 'report.png')", ([Uri]$file.FullName).AbsoluteUri -Wait
    }
  }
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
