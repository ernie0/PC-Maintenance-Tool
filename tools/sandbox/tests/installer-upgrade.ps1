# 安裝檔：靜默安裝舊版（帶公司設定檔與自動清理）→ 靜默升級到新版 → 確認版本、設定、自動清理、說明書
# → 用裝好的程式跑命令列匯出、開畫面 → 靜默解除安裝。
# 需要 Run-SandboxTest.ps1 -Installers <資料夾>，資料夾裡放兩個完整版安裝檔（PcMaintenance-Setup-<版本>.exe）。
. (Join-Path $PSScriptRoot 'common.ps1')
$installers = 'C:\pcm\installers'
$app = Join-Path $env:ProgramFiles 'PcMaintenance'
$programData = Join-Path $env:ProgramData 'PcMaintenance'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$run = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

function Installed { Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq '電腦維護工具' } | Select-Object -First 1 }
function Setup([string]$file, [string]$arguments) {
  $p = Start-Process $file -ArgumentList $arguments -PassThru -Wait
  return $p.ExitCode
}

try {
  Start-Sleep -Seconds 8
  Log "── 安裝檔升級（系統管理員：$(IsAdministrator)）"
  $setups = @(Get-ChildItem $installers -Filter 'PcMaintenance-Setup-*.exe' | Where-Object { $_.Name -notlike '*-lite.exe' } |
    Sort-Object { [version]($_.BaseName -replace '^PcMaintenance-Setup-', '') })
  if ($setups.Count -lt 2) { throw '安裝檔資料夾要有新舊兩個完整版安裝檔' }
  $old, $new = $setups[0], $setups[-1]
  Log "  舊版 $($old.Name)，新版 $($new.Name)"
  $settings = Join-Path $script:out 'company-settings.json'
  Set-Content $settings '{ "autoCleanIntervalDays": 3 }' -Encoding UTF8
  $settingsHash = (Get-FileHash $settings).Hash

  $code = Setup $old.FullName "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SETTINGS=`"$settings`" /TASKS=`"autoclean`" /LOG=`"$(Join-Path $script:out 'install-old.log')`""
  Check '靜默安裝舊版' ($code -eq 0 -and (Installed).DisplayVersion -eq ($old.BaseName -replace '^PcMaintenance-Setup-', '')) "結束代碼 $code，版本 $((Installed).DisplayVersion)"
  Check '公司設定檔已複製' ((Get-FileHash (Join-Path $programData 'settings.json')).Hash -eq $settingsHash)
  Check '自動清理已設定' ([bool](Get-ItemProperty $run -Name PcMaintenanceAutoClean -ErrorAction SilentlyContinue))

  $code = Setup $new.FullName "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=`"$(Join-Path $script:out 'install-new.log')`""
  $version = $new.BaseName -replace '^PcMaintenance-Setup-', ''
  Check '靜默升級到新版' ($code -eq 0 -and (Installed).DisplayVersion -eq $version) "結束代碼 $code，版本 $((Installed).DisplayVersion)"
  $exe = Join-Path $app 'PcMaintenance.exe'
  Log "  程式版本：$((Get-Item $exe).VersionInfo.ProductVersion)"
  Check '升級後公司設定檔沒被覆蓋' ((Get-FileHash (Join-Path $programData 'settings.json')).Hash -eq $settingsHash)
  Check '升級後自動清理還在' ([bool](Get-ItemProperty $run -Name PcMaintenanceAutoClean -ErrorAction SilentlyContinue))
  $manual = Get-Item (Join-Path $app '使用說明書.pdf') -ErrorAction SilentlyContinue
  Log "  說明書：$(if ($manual) { '{0:N0} bytes' -f $manual.Length } else { '沒有' })；開始功能表捷徑：$(@(Get-ChildItem "$env:ProgramData\Microsoft\Windows\Start Menu\Programs" -Filter '電腦維護工具*.lnk').Name -join '、')"
  Check '設定範例已更新成新版' ((Get-Content (Join-Path $programData 'settings.example.json') -Raw) -like '*delivery-optimization*')

  $reports = Join-Path $script:out 'reports'
  New-Item -ItemType Directory -Force $reports | Out-Null
  $p = Start-Process $exe -ArgumentList '--export-report', "$reports\" -PassThru -Wait
  Check '裝好的程式能匯出健檢報告' ($p.ExitCode -eq 0 -and (Get-ChildItem $reports -Filter '*.html')) "結束代碼 $($p.ExitCode)"
  $p = Start-Process $exe -ArgumentList '--export-system', "$reports\" -PassThru -Wait
  $csv = Get-ChildItem $reports -Filter '*.csv' | Select-Object -First 1
  Check '裝好的程式能匯出電腦配備 CSV（含新欄位）' ($p.ExitCode -eq 0 -and $csv -and (Get-Content $csv.FullName -TotalCount 1 -Encoding UTF8) -like '*安全性建議*') "結束代碼 $($p.ExitCode)"

  $script:exe = $exe
  Start-App
  $texts = Texts
  Check '畫面顯示新版與公司設定檔' (($texts -contains "版本 $version") -and ($texts | Where-Object { $_ -like '*公司設定檔*' })) (($texts | Where-Object { $_ -like '版本 *' -or $_ -like '設定：*' }) -join '／')
  Shot 'installed'
  Stop-App

  $code = Setup (Join-Path $app 'unins000.exe') '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  Start-Sleep -Seconds 5
  Check '靜默解除安裝' ($code -eq 0 -and -not (Installed) -and -not (Test-Path $exe)) "結束代碼 $code"
  Check '解除安裝後自動清理已移除' (-not (Get-ItemProperty $run -Name PcMaintenanceAutoClean -ErrorAction SilentlyContinue))
  Check '解除安裝後公司設定檔保留' (Test-Path (Join-Path $programData 'settings.json'))
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
