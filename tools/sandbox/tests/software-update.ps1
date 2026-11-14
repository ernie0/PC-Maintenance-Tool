# 軟體更新（winget）：Sandbox 預設沒有 winget，先從 GitHub 安裝；刻意裝一個舊版 7-Zip，
# 再從「軟體解除安裝 → 軟體更新」檢查、選取 7-Zip、按「更新」並確認，命令視窗跑完後 7-Zip 要變成新版，
# 重新檢查時不再列出。需要網路（GitHub、winget 的軟體來源）。
. (Join-Path $PSScriptRoot 'common.ps1')
$ProgressPreference = 'SilentlyContinue'
# 7-Zip comes as an MSI from winget, registered under a product code rather than a key named after it.
function SevenZipVersion {
  (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.DisplayName -like '7-Zip*' } | Select-Object -First 1).DisplayVersion
}
function Winget([string[]]$arguments) {
  $exe = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
  $p = Start-Process $exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
  return $p.ExitCode
}

try {
  Start-Sleep -Seconds 8
  Log "── 軟體更新（系統管理員：$(IsAdministrator)）"

  # winget itself: the App Installer bundle and its dependencies from winget-cli's latest release.
  $work = Join-Path $env:TEMP 'winget'
  New-Item -ItemType Directory -Force $work | Out-Null
  $release = Invoke-RestMethod 'https://api.github.com/repos/microsoft/winget-cli/releases/latest' -Headers @{ 'User-Agent' = 'pcm-sandbox-test' }
  $bundle = $release.assets | Where-Object { $_.name -like '*.msixbundle' } | Select-Object -First 1
  $deps = $release.assets | Where-Object { $_.name -eq 'DesktopAppInstaller_Dependencies.zip' } | Select-Object -First 1
  Invoke-WebRequest $deps.browser_download_url -OutFile (Join-Path $work 'deps.zip') -UseBasicParsing
  Expand-Archive (Join-Path $work 'deps.zip') (Join-Path $work 'deps') -Force
  Get-ChildItem (Join-Path $work 'deps') -Recurse -Filter '*.appx' | Where-Object { $_.FullName -match '\\x64\\' } | ForEach-Object { Add-AppxPackage $_.FullName }
  Invoke-WebRequest $bundle.browser_download_url -OutFile (Join-Path $work 'winget.msixbundle') -UseBasicParsing
  Add-AppxPackage (Join-Path $work 'winget.msixbundle')
  $wingetReady = Wait-Until { Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe') } 60
  Check '安裝 winget' ([bool]$wingetReady) "$($release.tag_name)"

  # An outdated 7-Zip to update: the newest version but one.
  $versions = @((& (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe') show --id 7zip.7zip --versions --source winget --accept-source-agreements --disable-interactivity) |
    Where-Object { $_ -match '^\d+(\.\d+)+\s*$' } | ForEach-Object { $_.Trim() })
  $old = $versions | Select-Object -Skip 1 -First 1
  $code = Winget @('install', '--id', '7zip.7zip', '--version', $old, '--exact', '--source', 'winget', '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
  Check "安裝舊版 7-Zip $old" ($code -eq 0 -and (SevenZipVersion)) "結束代碼 $code，版本 $(SevenZipVersion)，最新 $($versions[0])"

  Start-App
  Select-Item '軟體解除安裝' $CT::ListItem
  Select-Item '軟體更新' $CT::TabItem
  Press (Find-El '檢查更新' $CT::Button)
  $status = Wait-Until { Texts | Where-Object { $_ -like '有 * 個軟體可以更新*' -or $_ -like '已安裝的軟體都是最新版*' -or $_ -like '*無法*' -or $_ -like '這台電腦沒有*' } | Select-Object -First 1 } 180
  $rows = @(Rows '可以更新的軟體')
  Log "  $status；清單：$($rows -join ' ／ ')"
  $row = $rows | Where-Object { $_ -like '*7zip.7zip*' } | Select-Object -First 1
  Check '檢查更新列出舊版 7-Zip' ([bool]$row) "$row"
  Shot 'updates-checked'

  $grid = Find-El '可以更新的軟體' $CT::DataGrid
  $item = @($grid.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataItem)))) |
    Where-Object { ((@($_.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) | ForEach-Object { $_.Current.Name })) -join ' ') -like '*7zip.7zip*' } | Select-Object -First 1
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 800
  Press (Find-El '更新' $CT::Button)
  $confirm = Confirm-Dialog '更新軟體' 'update-confirm'
  Check '確認視窗說明要更新的版本' ($confirm -like "*7-Zip*$old*") $confirm
  $console = Find-Console '更新軟體'
  $before = [version](SevenZipVersion)
  $updated = Wait-Until { $v = SevenZipVersion; if ($v -and [version]$v -gt $before) { $v } } 300
  Start-Sleep -Seconds 5
  if ($console) { Shot-Console 'update-console' $console }
  Check '按「更新」後 7-Zip 變成新版' ([bool]$updated) "現在 $(SevenZipVersion)"
  if ($console) { Stop-Process -Id $console.ProcessId -Force -ErrorAction SilentlyContinue }

  Press (Find-El '檢查更新' $CT::Button)
  Start-Sleep -Seconds 2
  $null = Wait-Until { Texts | Where-Object { $_ -like '有 * 個軟體可以更新*' -or $_ -like '已安裝的軟體都是最新版*' } | Select-Object -First 1 } 180
  Check '重新檢查後不再列出 7-Zip' (-not (@(Rows '可以更新的軟體') | Where-Object { $_ -like '*7zip.7zip*' }))
  Shot 'updates-after'
  Stop-App
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
