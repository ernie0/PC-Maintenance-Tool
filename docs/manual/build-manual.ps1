# 產生使用說明書：python-docx 建 DOCX → Word 更新目錄並另存 PDF。
# 用法：pwsh -File docs\manual\build-manual.ps1 -Python <裝有 python-docx 的 python.exe> [-OutDir dist]
param([Parameter(Mandatory)][string]$Python, [string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$docx = Join-Path $OutDir "電腦維護工具使用說明書-$version.docx"
$pdf = [System.IO.Path]::ChangeExtension($docx, '.pdf')

& $Python -I -X utf8 (Join-Path $PSScriptRoot 'make_manual.py') --out $docx
if ($LASTEXITCODE) { throw '產生 DOCX 失敗' }

# 只結束這支腳本自己啟動的 Word；使用者原本開著的 Word 只關掉這份文件。
$before = @(Get-Process WINWORD -ErrorAction SilentlyContinue).Id
$word = New-Object -ComObject Word.Application
$ours = @(Get-Process WINWORD -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
try {
  $word.DisplayAlerts = 0
  $document = $word.Documents.Open($docx)
  foreach ($toc in $document.TablesOfContents) { $toc.Update() }
  $document.Fields.Update() | Out-Null
  $document.Save()
  $document.ExportAsFixedFormat($pdf, 17)
  $document.Close()
}
finally {
  if ($ours.Count -gt 0) { $word.Quit() }
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($word) | Out-Null
  # Wait until it is really gone, so a following run does not mistake it for the user's Word.
  foreach ($process in $ours) { $process.WaitForExit(15000) | Out-Null }
}
"DOCX：$docx"
"PDF：$pdf"
