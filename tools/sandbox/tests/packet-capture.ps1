# 系統工具「擷取網路封包」：確認後開出系統管理員命令視窗開始擷取；提早結束仍存成 pcapng 並刪掉 .etl；
# 已有其他擷取在進行時要說明原因、不動那個擷取。提早結束以結束 timeout 程序模擬按任意鍵。
. (Join-Path $PSScriptRoot 'common.ps1')
$captureDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) '網路封包擷取'
function Start-Capture {
  $before = @(Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | ForEach-Object ProcessId)
  Press (Find-El '擷取網路封包' $CT::Button)
  $null = Confirm-Dialog '擷取網路封包' 'capture-confirm'
  $console = Wait-Until { Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object { $_.CommandLine -like '*title 擷取網路封包*' -and $before -notcontains $_.ProcessId } | Select-Object -First 1 } 20
  if (-not $console) { throw '沒有開啟命令視窗' }
  return $console
}

try {
  Start-Sleep -Seconds 10
  Log "── 擷取網路封包（系統管理員：$(IsAdministrator)）"
  Start-App
  Select-Item '系統工具' $CT::ListItem
  Start-Sleep -Seconds 2
  $console = Start-Capture
  $etl = Wait-Until { Get-ChildItem $captureDir -Filter '*.etl' -ErrorAction SilentlyContinue | Select-Object -First 1 } 30
  Check '開始擷取（產生 .etl）' ([bool]$etl)
  try { Invoke-WebRequest 'http://example.com/' -UseBasicParsing | Out-Null } catch { Log "  產生流量失敗：$($_.Exception.Message)" }
  Start-Sleep -Seconds 3
  Shot-Console 'capture-running' $console
  $timeouts = @(Get-Process timeout -ErrorAction SilentlyContinue)
  Check '命令視窗正在等待（timeout）' ($timeouts.Count -gt 0)
  $timeouts | Stop-Process -Force
  $pcap = Wait-Until { Get-ChildItem $captureDir -Filter '*.pcapng' -ErrorAction SilentlyContinue | Select-Object -First 1 } 90
  if ($pcap) {
    $bytes = [IO.File]::ReadAllBytes($pcap.FullName)
    $magic = ($bytes[0..3] | ForEach-Object { $_.ToString('X2') }) -join ' '
    Check '提早結束後存成 pcapng' ($magic -eq '0A 0D 0D 0A' -and $bytes.Length -gt 2048) ("{0:N0} bytes，開頭 {1}" -f $bytes.Length, $magic)
  } else { Check '提早結束後存成 pcapng' $false '90 秒內沒有產生' }
  Start-Sleep -Seconds 3
  Check '.etl 已刪除' (-not (Get-ChildItem $captureDir -Filter '*.etl' -ErrorAction SilentlyContinue))
  Shot-Console 'capture-done' $console
  Stop-Process -Id $console.ProcessId -Force -ErrorAction SilentlyContinue

  & pktmon start --capture --comp nics --file-name (Join-Path $env:TEMP 'busy.etl') | Out-Null
  Start-Sleep -Seconds 2
  $busy = Start-Capture
  Start-Sleep -Seconds 5
  Shot-Console 'capture-busy' $busy
  Check '已有擷取在進行時不另外建立檔案' (-not (Get-ChildItem $captureDir -Filter '*.etl' -ErrorAction SilentlyContinue))
  Check '原本的擷取仍在進行' (Test-Path (Join-Path $env:TEMP 'busy.etl'))
  & pktmon stop | Out-Null
  Stop-Process -Id $busy.ProcessId -Force -ErrorAction SilentlyContinue
  Stop-App
}
catch {
  Log "✘ 測試中斷：$($_.Exception.Message)（第 $($_.InvocationInfo.ScriptLineNumber) 行）"
  try { Shot 'error' } catch { }
}
finally { Complete-Test }
