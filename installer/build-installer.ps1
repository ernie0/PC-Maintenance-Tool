# 測試 → 發佈 → 用 Inno Setup 產生兩個安裝檔與 SHA-256：
#   完整版 PcMaintenance-Setup-<版本>.exe：自帶 .NET 執行環境的單一 exe，任何 Windows 10／11 64 位元都能裝。
#   精簡版 PcMaintenance-Setup-<版本>-lite.exe：同一份程式，改用電腦上的 .NET 10 桌面執行環境，安裝檔約 4 MB；
#     電腦沒有執行環境時，安裝前會從 Microsoft 下載安裝（約 58 MB，需要網路）。
# 用法：pwsh -File installer\build-installer.ps1 [-Iscc "C:\Program Files\Inno Setup 7\ISCC.exe"]
param([string]$Iscc)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$publishLite = Join-Path $root 'artifacts\publish-lite'
$dist = Join-Path $root 'dist'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version

if (-not $Iscc) {
  $Iscc = @('Inno Setup 7', 'Inno Setup 6') |
    ForEach-Object { Join-Path $env:ProgramFiles "$_\ISCC.exe"; Join-Path ${env:ProgramFiles(x86)} "$_\ISCC.exe" } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw '找不到 Inno Setup 的 ISCC.exe，請用 -Iscc 指定' }

dotnet test (Join-Path $root 'tests\PcMaintenance.Core.Tests') -c Release --nologo
if ($LASTEXITCODE) { throw '測試沒有全部通過，停止打包' }

foreach ($folder in $publish, $publishLite) { if (Test-Path $folder) { Remove-Item $folder -Recurse -Force } }
$project = Join-Path $root 'src\PcMaintenance.App\PcMaintenance.App.csproj'
# 原生 DLL 留在 exe 旁邊而不是執行時解壓到暫存資料夾，防毒軟體比較不會攔。
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=embedded -o $publish --nologo
if ($LASTEXITCODE) { throw '發佈失敗（完整版）' }
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=embedded -o $publishLite --nologo
if ($LASTEXITCODE) { throw '發佈失敗（精簡版）' }

$iss = Join-Path $PSScriptRoot 'PcMaintenance.iss'
& $Iscc "/DPublishDir=$publish" "/DAppVersion=$version" "/O$dist" $iss
if ($LASTEXITCODE) { throw '安裝檔編譯失敗（完整版）' }
& $Iscc "/DPublishDir=$publishLite" "/DAppVersion=$version" "/DFrameworkDependent" "/O$dist" $iss
if ($LASTEXITCODE) { throw '安裝檔編譯失敗（精簡版）' }

foreach ($setup in (Join-Path $dist "PcMaintenance-Setup-$version.exe"), (Join-Path $dist "PcMaintenance-Setup-$version-lite.exe")) {
  $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
  "$hash  $(Split-Path -Leaf $setup)" | Set-Content "$setup.sha256" -Encoding ascii
  '安裝檔：{0}（{1:N1} MB）' -f $setup, ((Get-Item $setup).Length / 1MB)
  "SHA-256：$hash"
}
