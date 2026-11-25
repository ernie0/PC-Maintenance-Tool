# 遠端協助 P1 單機測試：在這台同時起被控端與控制端，走 127.0.0.1。
# 會看到「畫面中的畫面」（鏡中鏡）是正常的——代表控制端真的收到並畫出被控端的桌面。
# 用法：在 .worktrees\remote-assist 目錄下執行  powershell -ExecutionPolicy Bypass -File tools\start-remote-demo.ps1
# 兩台測試：在被控端那台跑 remote-agent（預設綁 0.0.0.0:18010），控制端開 remote-viewer 填被控端的區網位址。

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$port = 18010

Write-Host "建置被控端與控制端…" -ForegroundColor Cyan
dotnet build "$root\tools\remote-agent\remote-agent.csproj" -c Release -v quiet --nologo | Out-Null
dotnet build "$root\tools\remote-viewer\remote-viewer.csproj" -c Release -v quiet --nologo | Out-Null

$agentExe  = Get-ChildItem "$root\tools\remote-agent\bin\Release"  -Recurse -Filter remote-agent.exe  | Select-Object -First 1
$viewerExe = Get-ChildItem "$root\tools\remote-viewer\bin\Release" -Recurse -Filter remote-viewer.exe | Select-Object -First 1

Write-Host "啟動被控端（127.0.0.1:$port，只接受本機）…" -ForegroundColor Cyan
Start-Process $agentExe.FullName -ArgumentList @('--bind','127.0.0.1','--port',"$port")

Start-Sleep -Milliseconds 800
Write-Host "開啟控制端視窗，按『連線』即可看到本機桌面並操作。" -ForegroundColor Green
Start-Process $viewerExe.FullName -ArgumentList @('127.0.0.1',"$port")
