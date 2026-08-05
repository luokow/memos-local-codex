[CmdletBinding()]
param(
    [switch]$RunSelfTest
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\QwenLocalChat\QwenLocalChat.csproj'
$coreTests = Join-Path $root 'tests\QwenLocalChat.Tests\QwenLocalChat.Tests.csproj'
$uiTests = Join-Path $root 'tests\QwenLocalChat.UiTests\QwenLocalChat.UiTests.csproj'

dotnet run --project $coreTests -c Release
if ($LASTEXITCODE -ne 0) { throw "QwenLocalChat core tests failed with exit code $LASTEXITCODE" }

dotnet run --project $uiTests -c Release
if ($LASTEXITCODE -ne 0) { throw "QwenLocalChat UI regression tests failed with exit code $LASTEXITCODE" }

dotnet publish $project -c Release -o $root --nologo
if ($LASTEXITCODE -ne 0) { throw "QwenLocalChat publish failed with exit code $LASTEXITCODE" }

foreach ($obsoleteSymbol in @('QwenLocalChat.pdb', 'QwenLocalChat.Core.pdb')) {
    $symbolPath = Join-Path $root $obsoleteSymbol
    if (Test-Path -LiteralPath $symbolPath) { Remove-Item -LiteralPath $symbolPath -Force }
}

$exe = Join-Path $root 'QwenLocalChat.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Publish did not create $exe" }
$artifact = Get-Item -LiteralPath $exe
$digest = Get-FileHash -Algorithm SHA256 -LiteralPath $exe
$report = [ordered]@{
    path = $artifact.FullName
    length = $artifact.Length
    last_write_time = $artifact.LastWriteTime.ToString('o')
    sha256 = $digest.Hash
}
$reportPath = Join-Path $root 'diagnostics\publish-artifact.json'
[IO.Directory]::CreateDirectory((Split-Path -Parent $reportPath)) | Out-Null
[IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

if ($RunSelfTest) {
    $previousRoot = $env:QWEN_LOCAL_CHAT_ROOT
    try {
        $env:QWEN_LOCAL_CHAT_ROOT = $root
        $selfTest = Start-Process -FilePath $exe -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
        if ($selfTest.ExitCode -ne 0) { throw "Published QwenLocalChat self-test failed with exit code $($selfTest.ExitCode)" }
    }
    finally {
        $env:QWEN_LOCAL_CHAT_ROOT = $previousRoot
    }
}

Write-Host "Built $exe"
Write-Host "Artifact report: $reportPath"
