# 各測試共用的工具（在 Sandbox 裡以 Windows PowerShell 5.1 執行，檔案要存成 UTF-8 含 BOM，否則中文會變亂碼）。
# 用法：測試腳本開頭 . (Join-Path $PSScriptRoot 'common.ps1')，結尾在 finally 呼叫 Complete-Test。
$ErrorActionPreference = 'Stop'
$script:out = 'C:\pcm\out'
$script:log = Join-Path $script:out 'log.txt'
$script:shots = Join-Path $script:out 'shots'
$script:exe = 'C:\pcm\app\PcMaintenance.exe'
New-Item -ItemType Directory -Force $script:shots | Out-Null

function Log([string]$text) {
  $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $text
  # The host may be reading the file at the same moment.
  for ($i = 0; $i -lt 10; $i++) { try { Add-Content -Path $script:log -Value $line -Encoding UTF8 -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 300 } }
}
function Check([string]$name, [bool]$ok, [string]$detail = '') { Log ("{0} {1}{2}" -f $(if ($ok) { '✔' } else { '✘' }), $name, $(if ($detail) { "：$detail" } else { '' })) }
function IsAdministrator { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators') }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PcmWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dialog, int id);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@
$script:AE = [System.Windows.Automation.AutomationElement]
$script:CT = [System.Windows.Automation.ControlType]
$script:Scope = [System.Windows.Automation.TreeScope]

function Cond([string]$name, $type) {
  New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)),
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $type)))
}
function Find-El([string]$name, $type, $under = $script:root) {
  for ($i = 0; $i -lt 60; $i++) { $el = $under.FindFirst($Scope::Descendants, (Cond $name $type)); if ($el) { return $el }; Start-Sleep -Milliseconds 250 }
  throw "找不到 $name"
}
function Select-Item([string]$name, $type) { (Find-El $name $type).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 800 }
# A button whose text is shown may still be named otherwise for screen readers (the tools page names each 執行 after its tool).
function Press($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Texts($under = $script:root) { @($under.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) }
function Wait-Until([scriptblock]$test, [int]$seconds) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $seconds) { $v = & $test; if ($v) { return $v }; Start-Sleep -Milliseconds 500 }
  return $null
}
# Cells of a DataGrid, one string per row.
function Rows([string]$grid) {
  $g = Find-El $grid $CT::DataGrid
  @($g.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::DataItem)))) | ForEach-Object {
    (@($_.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Text))) | ForEach-Object { $_.Current.Name })) -join ' | '
  }
}
# Window-only picture: the program's window (or a dialog or console), never the screen.
function Shot([string]$name, [IntPtr]$hwnd = $script:hwnd) {
  $r = New-Object PcmWin+RECT; [PcmWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [PcmWin]::PrintWindow($hwnd, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save((Join-Path $script:shots "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
# The size the manual's screenshots are taken at.
function Start-App([int]$width = 1184, [int]$height = 772) {
  $script:proc = Start-Process $script:exe -PassThru
  for ($i = 0; $i -lt 120 -and $script:proc.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $script:proc.Refresh() }
  $script:hwnd = $script:proc.MainWindowHandle
  $script:root = $AE::FromHandle($script:hwnd)
  [PcmWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 20, 20, $width, $height, 0x0014) | Out-Null
  Start-Sleep -Seconds 2
}
function Stop-App {
  $script:proc.CloseMainWindow() | Out-Null
  $script:proc.WaitForExit(10000) | Out-Null
}
# MessageBox buttons show up only as panes in UI Automation; OK is control 1, so it is clicked by its ID.
function Confirm-Dialog([string]$title, [string]$shot) {
  $dialog = Wait-Until { $script:root.FindFirst($Scope::Descendants, (Cond $title $CT::Window)) } 10
  if (-not $dialog) { $dialog = Wait-Until { $AE::RootElement.FindFirst($Scope::Children, (Cond $title $CT::Window)) } 5 }
  if (-not $dialog) { throw "沒有出現「$title」確認視窗" }
  $dh = [IntPtr]$dialog.Current.NativeWindowHandle
  $text = (Texts ($AE::FromHandle($dh))) -join ' '
  if ($shot) { Shot $shot $dh }
  [PcmWin]::SendMessage([PcmWin]::GetDlgItem($dh, 1), 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
  return $text
}
# The elevated console a tool opened, found by the title the program gives it.
function Find-Console([string]$title) {
  Wait-Until { Get-CimInstance Win32_Process -Filter "Name='cmd.exe'" | Where-Object { $_.CommandLine -like "*title $title*" } | Select-Object -First 1 } 20
}
function Shot-Console([string]$name, $console) {
  $p = Get-Process -Id $console.ProcessId -ErrorAction SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) { Shot $name $p.MainWindowHandle } else { Log "  （$name：命令視窗沒有可截圖的視窗）" }
}
function Complete-Test {
  Log 'DONE'
  Set-Content (Join-Path $script:out 'DONE') 'done' -Encoding UTF8
  Start-Sleep -Seconds 3
  shutdown.exe /s /t 5
}
