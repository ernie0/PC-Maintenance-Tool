# 在 Windows Sandbox 裡跑一個測試腳本：發佈目前的程式（自帶執行環境的單一 exe）、啟動 Sandbox、等測試寫出 DONE。
# 本機螢幕一律不擷取；截圖由測試在 Sandbox 裡只拍程式自己的視窗（PrintWindow）。
# 用法：pwsh -File tools\sandbox\Run-SandboxTest.ps1 -Test system-changes [-SkipPublish]
param(
  [Parameter(Mandatory)][string]$Test,
  [switch]$SkipPublish,
  [int]$MinutesToFinish = 20
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$work = Join-Path $root 'artifacts\sandbox'
$app = Join-Path $work 'app'
$out = Join-Path $work 'out'
$tests = Join-Path $PSScriptRoot 'tests'
if (-not (Test-Path (Join-Path $tests "$Test.ps1"))) { throw "找不到測試 tests\$Test.ps1" }
if ((wsb list 2>&1 | Out-String).Trim()) { throw '已有 Windows Sandbox 在執行，Windows 一次只能開一個' }

if (-not $SkipPublish) {
  dotnet publish (Join-Path $root 'src\PcMaintenance.App') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o $app --nologo
  if ($LASTEXITCODE) { throw '發佈失敗' }
}
# The previous run's results are kept beside the new ones, named after the time they were moved.
if (Test-Path $out) { Rename-Item $out ("out-{0:yyyyMMdd-HHmmss}" -f (Get-Item $out).LastWriteTime) }
New-Item -ItemType Directory -Force $out | Out-Null

$config = Join-Path $work 'test.wsb'
@"
<Configuration>
  <MemoryInMB>4096</MemoryInMB>
  <VGpu>Disable</VGpu>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MappedFolders>
    <MappedFolder><HostFolder>$app</HostFolder><SandboxFolder>C:\pcm\app</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$tests</HostFolder><SandboxFolder>C:\pcm\tests</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$out</HostFolder><SandboxFolder>C:\pcm\out</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\pcm\tests\$Test.ps1</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content $config -Encoding utf8

# The sandbox window sometimes loses its connection before anyone signs in ("與 Windows 沙箱環境的連線已中斷。是否要重新連線?")
# and then waits forever; its 重新連線 (Reconnect) button brings the session back and the sign-in goes ahead.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Invoke-Reconnect {
  foreach ($p in Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
    if ($p.MainWindowHandle -eq 0) { continue }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $button = $buttons | Where-Object { $_.Current.Name -in '重新連線', 'Reconnect' } | Select-Object -First 1
    if ($button) { $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
  }
  return $false
}

function Stop-Sandboxes {
  foreach ($id in ((wsb list 2>&1 | Out-String).Trim() -split '\s+' | Where-Object { $_ })) { wsb stop --id $id 2>&1 | Out-Null }
  Get-Process WindowsSandboxRemoteSession -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

# The sandbox user sometimes never signs in on the first boot; after five minutes it is restarted, three times at most.
$log = Join-Path $out 'log.txt'
$done = Join-Path $out 'DONE'
for ($attempt = 1; $attempt -le 3; $attempt++) {
  Start-Process "$env:windir\System32\WindowsSandbox.exe" -ArgumentList ('"' + $config + '"')
  $sw = [Diagnostics.Stopwatch]::StartNew()
  # Pressing again too soon cuts short a reconnect that is still under way.
  $pressed = -60
  while ($sw.Elapsed.TotalMinutes -lt 5 -and -not (Test-Path $log)) {
    Start-Sleep -Seconds 10
    if ($sw.Elapsed.TotalSeconds - $pressed -ge 60 -and (Invoke-Reconnect)) { $pressed = $sw.Elapsed.TotalSeconds; '按了 Sandbox 視窗的「重新連線」' }
  }
  if (Test-Path $log) { break }
  "第 $attempt 次：5 分鐘內沒有登入，關閉後重試"
  Stop-Sandboxes
  Start-Sleep -Seconds 20
}
if (-not (Test-Path $log)) { throw '三次都沒有登入 Sandbox；Sandbox 在電腦有人使用時比較容易登入，請稍後再試' }

$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalMinutes -lt $MinutesToFinish -and -not (Test-Path $done)) { Start-Sleep -Seconds 10 }
if (-not (Test-Path $done)) { Stop-Sandboxes; "超過 $MinutesToFinish 分鐘沒有完成，已關閉 Sandbox" }
# Read without locking: the sandbox may still be writing (a tail -f here once made its writes fail).
$stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
try { (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $stream.Dispose() }
"結果與截圖：$out"
