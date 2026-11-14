# 0.7.0 的資訊人員檢查：
# - 登入時匯出（--auto-export）把電腦配備、軟體清單、瀏覽器擴充功能存到公司設定的資料夾，同一天第二次略過；
# - 開機與背景標出從暫存資料夾執行、沒有數位簽章的項目，命令列匯出也有「需要注意」欄；
# - 瀏覽器擴充功能分頁讀得到 Chrome 設定檔裡的擴充功能，標出不是從商店安裝的；
# - 網路診斷試連網路磁碟機（一個連得到、一個連不到）與網路印表機（連不到）；
# - 電腦資訊的檔案備份、複製求助資訊；一鍵維護的 Windows 版本與檔案備份；
# - 系統工具「電腦不睡眠或自己醒來」開啟命令視窗；
# - 多台電腦頁讀取公司資料夾，列出這台電腦與它的軟體、擴充功能。
# 截圖給說明書用：Sandbox 是乾淨的 Windows，不會拍到自己電腦的資料。
. (Join-Path $PSScriptRoot 'common.ps1')

function Scroll-Bottom {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
  foreach ($el in @($script:root.FindAll($Scope::Descendants, $cond))) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($p.Current.VerticallyScrollable -and $el.Current.ControlType -ne $CT::DataGrid -and $el.Current.ControlType -ne $CT::List) { $p.SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 800; return $true }
  }
  return $false
}
# The innermost scrollable list on the page (the network check's results), scrolled to its end.
function Scroll-LastList {
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
  $el = @($script:root.FindAll($Scope::Descendants, $cond)) | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).Current.VerticallyScrollable } | Select-Object -Last 1
  if ($el) { $el.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 100); Start-Sleep -Milliseconds 800 }
}

try {
  Start-Sleep -Seconds 8
  Log "── 資訊人員檢查（系統管理員：$(IsAdministrator)）"
  $today = Get-Date -Format 'yyyyMMdd'

  # An unsigned program started from the temporary folder at logon.
  $helper = Join-Path $env:TEMP 'setup-helper\helper.exe'
  New-Item -ItemType Directory -Force (Split-Path $helper) | Out-Null
  Copy-Item $script:exe $helper
  New-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name '更新小幫手' -Value "`"$helper`" --background" -Force | Out-Null

  # A Chrome profile with a store extension, one another program added from the store, and one loaded unpacked.
  $profile = Join-Path $env:LOCALAPPDATA 'Google\Chrome\User Data\Default'
  $store = Join-Path $profile 'Extensions\aaaabbbbccccddddeeeeffffgggghhhh\1.4.2_0'
  New-Item -ItemType Directory -Force $store, 'C:\ext-dev' | Out-Null
  Set-Content (Join-Path $store 'manifest.json') '{ "name": "翻譯小幫手", "version": "1.4.2", "update_url": "https://clients2.google.com/service/update2/crx" }' -Encoding UTF8
  Set-Content 'C:\ext-dev\manifest.json' '{ "name": "比價助手", "version": "0.3.1" }' -Encoding UTF8
  Set-Content (Join-Path $profile 'Secure Preferences') (@'
{ "extensions": { "settings": {
  "aaaabbbbccccddddeeeeffffgggghhhh": { "location": 1, "path": "aaaabbbbccccddddeeeeffffgggghhhh\\1.4.2_0", "disable_reasons": [] },
  "ghbmnnjooekpmoecnnnilnnbdlolhkhi": { "location": 6, "path": "ghbmnnjooekpmoecnnnilnnbdlolhkhi\\1.9", "from_webstore": true,
    "manifest": { "name": "文件離線版", "version": "1.9", "update_url": "https://clients2.google.com/service/update2/crx" } },
  "zzzzyyyyxxxxwwwwvvvvuuuuttttssss": { "location": 4, "path": "C:\\ext-dev", "state": 1 }
} } }
'@) -Encoding UTF8

  # A share this PC serves and maps as Z:, a remembered Y: on a server that does not exist, and a printer on an address nobody answers.
  New-Item -ItemType Directory -Force 'C:\共用資料' | Out-Null
  try { New-SmbShare -Name '共用資料' -Path 'C:\共用資料' -FullAccess 'Everyone' -ErrorAction Stop | Out-Null } catch { Log "  建立共用資料夾失敗：$($_.Exception.Message)" }
  $net = (cmd /c 'net use Z: \\localhost\共用資料 /persistent:yes 2>&1') -join ' '
  Log "  net use Z:：$net"
  New-Item 'HKCU:\Network\Y' -Force | Out-Null
  New-ItemProperty 'HKCU:\Network\Y' -Name 'RemotePath' -Value '\\192.0.2.10\資料' -Force | Out-Null
  # A printer on an address nobody answers; any driver Windows has will do, since it is only tried over the network.
  try { Add-PrinterPort -Name 'IP_192.0.2.50' -PrinterHostAddress '192.0.2.50' -ErrorAction Stop } catch { Log "  加入印表機連接埠失敗：$($_.Exception.Message)" }
  try { Add-PrinterDriver -Name 'Generic / Text Only' -ErrorAction Stop } catch { }
  $added = $null
  foreach ($driver in @('Generic / Text Only') + @(Get-PrinterDriver | ForEach-Object Name)) {
    try { Add-Printer -Name '事務機' -DriverName $driver -PortName 'IP_192.0.2.50' -ErrorAction Stop; $added = $driver; break } catch { }
  }
  Log "  網路印表機：$(if ($added) { "已用驅動程式「$added」加入" } else { "加不進去（可用的驅動程式：$((Get-PrinterDriver | ForEach-Object Name) -join '、')）" })"

  # The company settings with an inventory folder, as IT would deploy them.
  New-Item -ItemType Directory -Force 'C:\ProgramData\PcMaintenance', 'C:\電腦盤點' | Out-Null
  Set-Content 'C:\ProgramData\PcMaintenance\settings.json' '{ "inventoryFolder": "C:\\電腦盤點" }' -Encoding UTF8

  # Logon export, twice: the second run the same day is skipped.
  $p = Start-Process $script:exe -ArgumentList '--auto-export' -PassThru -Wait
  $files = @(Get-ChildItem 'C:\電腦盤點' -Filter '*.csv' | Sort-Object Name)
  $names = ($files | ForEach-Object Name) -join '、'
  Check '登入時匯出寫出三個檔案' ($p.ExitCode -eq 0 -and $files.Count -eq 3 -and
    (Test-Path "C:\電腦盤點\電腦配備-$env:COMPUTERNAME-$today.csv") -and (Test-Path "C:\電腦盤點\軟體清單-$env:COMPUTERNAME-$today.csv") -and
    (Test-Path "C:\電腦盤點\瀏覽器擴充功能-$env:COMPUTERNAME-$today.csv")) "結束代碼 $($p.ExitCode)，$names"
  $system = Import-Csv "C:\電腦盤點\電腦配備-$env:COMPUTERNAME-$today.csv" -Encoding UTF8
  Check '電腦配備有 Windows 11、備份與上次開機欄' ($system.'Windows 11 升級' -eq '已是 Windows 11' -and $system.'備份' -eq '沒有偵測到' -and $system.'上次開機' -match '^\d{4}-') `
    "Windows 11 升級＝$($system.'Windows 11 升級')；備份＝$($system.'備份')；上次開機＝$($system.'上次開機')"
  $ext = @(Import-Csv "C:\電腦盤點\瀏覽器擴充功能-$env:COMPUTERNAME-$today.csv" -Encoding UTF8)
  Check '擴充功能清單有三個，來源正確' ($ext.Count -eq 3 -and ($ext | Where-Object { $_.'名稱' -eq '比價助手' -and $_.'來源' -like '開發人員模式*' }) -and
    ($ext | Where-Object { $_.'名稱' -eq '文件離線版' -and $_.'來源' -eq 'Chrome 線上應用程式商店（由其他程式加入）' })) (($ext | ForEach-Object { "$($_.'名稱')：$($_.'來源')" }) -join '；')
  $before = $files | ForEach-Object LastWriteTimeUtc
  $p2 = Start-Process $script:exe -ArgumentList '--auto-export' -PassThru -Wait
  $after = @(Get-ChildItem 'C:\電腦盤點' -Filter '*.csv' | Sort-Object Name) | ForEach-Object LastWriteTimeUtc
  $logText = Get-Content (Get-ChildItem "$env:LOCALAPPDATA\PcMaintenance\logs" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName -Encoding UTF8 | Select-Object -Last 3
  Check '同一天第二次登入略過' ($p2.ExitCode -eq 0 -and (Compare-Object $before $after -SyncWindow 0).Count -eq 0 -and ($logText -join ' ') -like '*未滿 1 天*') (($logText -join ' ') -replace '^.*自動匯出', '自動匯出')

  # Start-up entries exported from the command line carry what needs attention.
  $startupCsv = 'C:\pcm\out\startup.csv'
  Start-Process $script:exe -ArgumentList '--export-startup', $startupCsv -Wait
  $helperRow = Import-Csv $startupCsv -Encoding UTF8 | Where-Object { $_.'名稱' -eq '更新小幫手' }
  Check '命令列匯出開機項目有需要注意欄' ($helperRow.'需要注意' -eq '從暫存資料夾執行；沒有數位簽章') "$($helperRow.'需要注意')"

  Start-App
  Select-Item '開機與背景' $CT::ListItem
  $count = Wait-Until { Texts | Where-Object { $_ -like '*項需要注意*' -or $_ -like '*數位簽章都正常*' } | Select-Object -First 1 } 120
  (Find-El '只看需要注意的' $CT::CheckBox).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
  Start-Sleep -Seconds 1
  $marked = Texts | Where-Object { $_ -eq '從暫存資料夾執行；沒有數位簽章' }
  Check '開機與背景標出暫存資料夾裡沒有簽章的程式' ([bool]$marked) "$count"
  $ms = Texts | Where-Object { $_ -like '標示為 Microsoft*' }
  Check 'Windows 內建的服務沒有被誤標' (-not $ms) "$(($ms | Select-Object -Unique) -join '；')"
  $grid = $script:root.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataGrid)))
  $row = @($grid.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataItem)))) |
    Where-Object { $_.FindFirst($Scope::Descendants, (Cond '更新小幫手' $CT::Text)) } | Select-Object -First 1
  if ($row) { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1 }
  Shot 'startup-attention'

  Select-Item '軟體解除安裝' $CT::ListItem
  Select-Item '瀏覽器擴充功能' $CT::TabItem
  $status = Wait-Until { Texts | Where-Object { $_ -like '共 * 個擴充功能*' } | Select-Object -First 1 } 30
  Check '瀏覽器擴充功能分頁：3 個，1 個不是從商店安裝' ("$status" -like '共 3 個擴充功能*其中 1 個不是從瀏覽器的商店安裝*') "$status"
  Shot 'extensions'

  Select-Item '工作管理員' $CT::ListItem
  Select-Item '網路' $CT::TabItem
  Select-Item '網路診斷' $CT::ListItem
  Press (Find-El '開始診斷' $CT::Button)
  $null = Wait-Until { Texts | Where-Object { $_ -like '檢查完成*' } | Select-Object -First 1 } 90
  Start-Sleep -Seconds 1
  $drives = Texts | Where-Object { $_ -like 'Y:*' -or $_ -like '*個都連得到*' } | Select-Object -First 1
  $printer = Texts | Where-Object { $_ -like '「事務機」*' } | Select-Object -First 1
  Check '網路診斷：Y: 連不到、Z: 正常' ("$drives" -like 'Y:（\\192.0.2.10\資料）連不到 192.0.2.10*' -and "$drives" -like '*其餘 1 個正常*') "$drives"
  Check '網路診斷：網路印表機連不到' ("$printer" -like '「事務機」連不到 192.0.2.50*') "$printer"
  Scroll-LastList
  Shot 'network-shares'

  Select-Item '電腦資訊' $CT::ListItem
  $null = Wait-Until { Texts | Where-Object { $_ -eq '檔案備份' } } 60
  Press (Find-El '複製求助資訊' $CT::Button)
  Start-Sleep -Milliseconds 800
  $help = @(Get-Clipboard | Where-Object { $_ })
  Check '複製求助資訊' ($help.Count -ge 7 -and $help[0] -eq "電腦名稱：$env:COMPUTERNAME" -and ($help | Where-Object { $_ -like 'IP 位址：*' })) (($help | ForEach-Object { ($_ -split '：')[0] }) -join '、')
  $backup = Texts | Where-Object { $_ -like '沒有偵測到 OneDrive 資料夾備份*' } | Select-Object -First 1
  Check '電腦資訊：沒有備份時說明' ([bool]$backup)
  Scroll-Bottom | Out-Null
  Shot 'info-backup'

  Select-Item '一鍵維護' $CT::ListItem
  Press (Find-El '一鍵掃描' $CT::Button)
  $null = Wait-Until { Texts | Where-Object { $_ -eq '這台電腦狀態良好' -or $_ -like '發現 * 項可以一鍵處理*' -or $_ -like '有 * 項建議，請看下方說明' } } 300
  Start-Sleep -Seconds 2
  $version = Texts | Where-Object { $_ -like 'Windows 11 *' -or $_ -like 'Windows 10 已結束支援*這台電腦*' } | Select-Object -First 1
  $noBackup = Texts | Where-Object { $_ -eq '沒有偵測到檔案備份' } | Select-Object -First 1
  Check '一鍵維護：Windows 版本有結果' ([bool]$version) "$version"
  Check '一鍵維護：沒有備份時提出建議' ([bool]$noBackup)
  Scroll-Bottom | Out-Null
  Shot 'maintenance-new-checks'

  Select-Item '系統工具' $CT::ListItem
  Press (Find-El '電腦不睡眠或自己醒來' $CT::Button)
  $console = Find-Console '電腦不睡眠或自己醒來'
  Start-Sleep -Seconds 6
  Check '電腦不睡眠：開啟系統管理員命令視窗' ([bool]$console)
  if ($console) { Shot-Console 'sleep-console' $console; Stop-Process -Id $console.ProcessId -Force -ErrorAction SilentlyContinue }

  Select-Item '多台電腦' $CT::ListItem
  $fleet = Wait-Until { Texts | Where-Object { $_ -like '共 * 台電腦*' } | Select-Object -First 1 } 30
  Check '多台電腦讀取公司資料夾' ("$fleet" -like '共 1 台電腦*') "$fleet"
  Shot 'fleet-one'
  Select-Item '軟體與擴充功能' $CT::TabItem
  $soft = Texts | Where-Object { $_ -like '1 台電腦的軟體清單*' } | Select-Object -First 1
  Check '多台電腦：軟體與擴充功能' ("$soft" -like '*3 個瀏覽器擴充功能*') "$soft"
  Stop-App
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
