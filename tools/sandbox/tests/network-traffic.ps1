# 網路頁「程式流量」：已知大小的下載（Microsoft 的 .NET 執行環境安裝檔，60,035,296 bytes）要記在 PowerShell 名下，
# ETW 工作階段只在這個畫面開著時存在，離開畫面與關閉程式後都要消失。
. (Join-Path $PSScriptRoot 'common.ps1')
$session = 'PcMaintenance-NetworkTraffic'
$runtimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.14/windowsdesktop-runtime-10.0.14-win-x64.exe'
function SessionRunning { [bool]((logman query -ets) -match [regex]::Escape($session)) }

try {
  Start-Sleep -Seconds 10
  Log "── 程式流量（系統管理員：$(IsAdministrator)）"
  Start-App
  Select-Item '工作管理員' $CT::ListItem
  Select-Item '網路' $CT::TabItem
  Start-Sleep -Seconds 2
  Check '在「連線」檢視時沒有流量工作階段' (-not (SessionRunning))
  Select-Item '程式流量' $CT::ListItem
  Start-Sleep -Seconds 3
  Check '切到「程式流量」後開始統計' (SessionRunning)

  $file = Join-Path $env:TEMP 'runtime.exe'
  $ProgressPreference = 'SilentlyContinue'
  Invoke-WebRequest $runtimeUrl -OutFile $file -UseBasicParsing
  Log ("  下載 {0:N0} bytes" -f (Get-Item $file).Length)
  Start-Sleep -Seconds 5
  $rows = @(Rows '程式流量')
  Log "  清單：$($rows -join ' ／ ')"
  $ps = $rows | Where-Object { $_ -like '*PowerShell*' } | Select-Object -First 1
  Check 'PowerShell 的已下載接近 57.3 MB' ($ps -match '\| 5[6-9](\.\d)? MB \|') $ps
  Shot 'traffic'

  Select-Item '連線' $CT::ListItem
  Start-Sleep -Seconds 2
  Check '離開「程式流量」後停止統計' (-not (SessionRunning))
  Select-Item '程式流量' $CT::ListItem
  Start-Sleep -Seconds 3
  Check '再回到「程式流量」重新開始' (SessionRunning)
  Stop-App
  Start-Sleep -Seconds 2
  Check '關閉程式後沒有留下工作階段' (-not (SessionRunning))
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
