[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\QwenLocalChat.WinUI\QwenLocalChat.WinUI.csproj'
$tests = Join-Path $root 'tests\QwenLocalChat.Tests\QwenLocalChat.Tests.csproj'
$uiBaselineTest = Join-Path $root 'tests\verify-winui-ui-baseline.ps1'
$uiBaselinePath = Join-Path $root 'docs\winui-ui-baseline.json'
$output = Join-Path $root 'src\QwenLocalChat.WinUI\bin\x64\Debug\net9.0-windows10.0.26100.0\win-x64'
$buildDll = Join-Path $output 'QwenLocalChat.WinUI.dll'
$appxDll = Join-Path $output 'AppX\QwenLocalChat.WinUI.dll'
$appxExe = Join-Path $output 'AppX\QwenLocalChat.WinUI.exe'

powershell -NoProfile -ExecutionPolicy Bypass -File $uiBaselineTest
if ($LASTEXITCODE -ne 0) { throw "WinUI UI baseline validation failed with exit code $LASTEXITCODE" }

$uiBaseline = Get-Content -Raw -Encoding UTF8 -LiteralPath $uiBaselinePath | ConvertFrom-Json

dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) { throw "QwenLocalChat core tests failed with exit code $LASTEXITCODE" }

# The loose-layout executable cannot be replaced while the desktop app is open.
# Stop only the process launched from this exact AppX output; the separate llama
# service is intentionally outside this target and remains running.
$runningApp = Get-Process -Name 'QwenLocalChat.WinUI' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and [System.IO.Path]::GetFullPath($_.Path) -eq [System.IO.Path]::GetFullPath($appxExe) }
foreach ($process in $runningApp) {
    Stop-Process -Id $process.Id -Force
}

$stopDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $remainingApp = Get-Process -Name 'QwenLocalChat.WinUI' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and [System.IO.Path]::GetFullPath($_.Path) -eq [System.IO.Path]::GetFullPath($appxExe) }
    if (-not $remainingApp) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $stopDeadline)

if ($remainingApp) {
    throw "Qwen Local AppX process is still locking the deployment target: $appxExe"
}

dotnet run --project $project -c Debug -p:Platform=x64 -p:WinAppRunNoLaunch=true
if ($LASTEXITCODE -ne 0) { throw "WinUI loose-package deployment failed with exit code $LASTEXITCODE" }

if (-not (Test-Path -LiteralPath $buildDll)) { throw "Build output is missing: $buildDll" }
if (-not (Test-Path -LiteralPath $appxDll)) { throw "Registered AppX output is missing: $appxDll" }

$buildHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $buildDll).Hash
$appxHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $appxDll).Hash
if ($buildHash -ne $appxHash) {
    throw "WinUI build/AppX mismatch: build=$buildHash appx=$appxHash"
}

[pscustomobject]@{
    BuildDll = $buildDll
    AppxDll = $appxDll
    Sha256 = $appxHash
    RegisteredPackage = $uiBaseline.acceptanceTarget.aumid
} | ConvertTo-Json
