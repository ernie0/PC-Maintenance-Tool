# 會改動系統的 0.5.0 功能：
# - 舊版 Windows 交給 Windows 磁碟清理移除，C:\Users 不會經由 Windows.old 裡的連結被刪，磁碟清理的選取設定事後收回；
# - 傳遞最佳化快取只刪超過 24 小時的檔案；
# - 清除卡住的列印工作：佇列檔刪除、列印服務重新啟動；
# - 時間慢 10 分鐘時網路診斷要發現，「校正電腦時間」後恢復一致。
# 最後拍電腦資訊頁給說明書用。
. (Join-Path $PSScriptRoot 'common.ps1')
function FolderBytes([string]$path) { [long](Get-ChildItem $path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum }
function ClockLine { Texts | Where-Object { $_ -like '和標準時間相差*' -or $_ -eq '和標準時間一致' -or $_ -like '連不到時間伺服器*' } | Select-Object -First 1 }
function Run-Diagnosis {
  Select-Item '工作管理員' $CT::ListItem
  Select-Item '網路' $CT::TabItem
  Select-Item '網路診斷' $CT::ListItem
  Press (Find-El '開始診斷' $CT::Button)
  $done = Wait-Until { Texts | Where-Object { $_ -like '檢查完成*' } | Select-Object -First 1 } 60
  Start-Sleep -Seconds 1
  return $done
}

try {
  Start-Sleep -Seconds 8
  Log "── 會改動系統的功能（系統管理員：$(IsAdministrator)）"

  # A previous installation as Windows leaves one: files, plus a junction back to the live C:\Users.
  New-Item -ItemType Directory -Force 'C:\Windows.old\Windows\System32', 'C:\Windows.old\Users\Old' | Out-Null
  1..3 | ForEach-Object { [IO.File]::WriteAllBytes("C:\Windows.old\Windows\System32\f$_.bin", (New-Object byte[] 5MB)) }
  cmd /c mklink /J "C:\Windows.old\Documents and Settings" "C:\Users" | Out-Null
  Set-Content 'C:\Users\Public\keep-me.txt' 'must survive'
  # Delivery Optimization cache files old enough to clean, and one from just now that must stay.
  $do = "$env:windir\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"
  New-Item -ItemType Directory -Force "$do\test" | Out-Null
  1..2 | ForEach-Object { $f = "$do\test\old$_.bin"; [IO.File]::WriteAllBytes($f, (New-Object byte[] 3MB)); (Get-Item $f).LastWriteTime = (Get-Date).AddDays(-3); (Get-Item $f).CreationTime = (Get-Date).AddDays(-3) }
  [IO.File]::WriteAllBytes("$do\test\new.bin", (New-Object byte[] 1MB))
  Log "  準備：Windows.old $(FolderBytes 'C:\Windows.old') bytes；傳遞最佳化測試檔 3 個"

  Start-App
  Log "  $((Texts | Where-Object { $_ -like '版本 *' }) -join '')"

  # ── Junk: scan, then clean only the two new categories.
  Select-Item '垃圾檔清理' $CT::ListItem
  Press (Find-El '掃描' $CT::Button)
  $null = Wait-Until { Texts | Where-Object { $_ -like '已勾選的項目約可釋放*' -or $_ -eq '沒有勾選可以清理的項目。' } } 180
  Start-Sleep -Seconds 3
  $boxes = @($script:root.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::CheckBox))))
  Log "  清理項目：$(($boxes | ForEach-Object { $_.Current.Name }) -join '、')"
  foreach ($box in $boxes) {
    $want = $box.Current.Name -like '傳遞最佳化快取*' -or $box.Current.Name -like '舊版 Windows*'
    $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($want -ne ($toggle.Current.ToggleState -eq 'On') -and $box.Current.IsEnabled) { $toggle.Toggle(); Start-Sleep -Milliseconds 200 }
  }
  Shot 'junk-scanned'
  Press (Find-El '清理已勾選項目' $CT::Button)
  $confirm = Confirm-Dialog '確認清理' 'junk-confirm'
  Check '確認視窗提醒不能退回舊版' ($confirm -like '*不能再退回*')
  $null = Wait-Until { Texts | Where-Object { $_ -like '已刪除 *個項目*' } } 900
  Start-Sleep -Seconds 2
  Log "  清理結果：$((Texts | Where-Object { $_ -like '已刪除 *' }) -join ' ／ ')"
  Shot 'junk-cleaned'
  Check 'Windows.old 已移除' (-not (Test-Path 'C:\Windows.old')) "剩 $(if (Test-Path 'C:\Windows.old') { FolderBytes 'C:\Windows.old' } else { 0 }) bytes"
  Check 'C:\Users 沒有被連帶刪除' ((Test-Path 'C:\Users\Public\keep-me.txt') -and (Test-Path "$env:USERPROFILE\Desktop"))
  Check '傳遞最佳化：舊檔刪除、新檔保留' ((-not (Test-Path "$do\test\old1.bin")) -and (-not (Test-Path "$do\test\old2.bin")) -and (Test-Path "$do\test\new.bin"))
  $flags = @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches' | Where-Object { (Get-ItemProperty $_.PSPath).PSObject.Properties.Name -contains 'StateFlags4242' })
  Check '磁碟清理的選取設定已收回' ($flags.Count -eq 0) "$($flags.Count) 個項目還留著"

  # ── Print queue: a stuck job is a pair of files in spool\PRINTERS.
  $spool = "$env:windir\System32\spool\PRINTERS"
  Set-Content "$spool\FP99999.SPL" 'stuck job'
  Set-Content "$spool\FP99999.SHD" 'stuck job'
  Select-Item '系統工具' $CT::ListItem
  Press (Find-El '清除卡住的列印工作' $CT::Button)
  $null = Confirm-Dialog '清除卡住的列印工作' 'print-confirm'
  $console = Find-Console '清除列印工作'
  $null = Wait-Until { (Get-Service Spooler).Status -eq 'Running' -and -not (Test-Path "$spool\FP99999.SPL") } 60
  Start-Sleep -Seconds 3
  Check '列印佇列已清空、列印服務重新啟動' ((-not (Test-Path "$spool\FP99999.SPL")) -and (Get-Service Spooler).Status -eq 'Running') "列印服務 $((Get-Service Spooler).Status)"
  if ($console) { Shot-Console 'print-console' $console; Stop-Process -Id $console.ProcessId -Force -ErrorAction SilentlyContinue }

  # ── Clock: 10 minutes slow, found by the network check, fixed by the tool.
  Stop-Service vmictimesync -Force -ErrorAction SilentlyContinue
  Set-Date (Get-Date).AddMinutes(-10) | Out-Null
  Start-Sleep -Seconds 5
  Log "  把時間調慢 10 分鐘後：$(Get-Date -Format 'HH:mm:ss')"
  Log "  診斷：$(Run-Diagnosis)"
  $clock = ClockLine
  Check '診斷發現電腦時間慢約 10 分鐘' ([bool]($clock -match '(9|10) 分.*電腦慢')) "$clock"
  Shot 'diagnosis-clock'
  Select-Item '系統工具' $CT::ListItem
  Press (Find-El '校正電腦時間' $CT::Button)
  $console = Find-Console '校正電腦時間'
  Start-Sleep -Seconds 20
  if ($console) { Shot-Console 'time-console' $console; Stop-Process -Id $console.ProcessId -Force -ErrorAction SilentlyContinue }
  Log "  診斷：$(Run-Diagnosis)"
  $clock = ClockLine
  Check '校正後時間正確' ($clock -eq '和標準時間一致' -or $clock -match '相差 \d+ 秒') "$clock"
  Shot 'diagnosis-ok'

  Select-Item '電腦資訊' $CT::ListItem
  $null = Wait-Until { Texts | Where-Object { $_ -eq '上次更新' } } 60
  Start-Sleep -Seconds 2
  Shot 'computer-info'
  Stop-App
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
