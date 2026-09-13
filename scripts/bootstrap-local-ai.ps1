# Create the on-disk layout a cloned checkout needs before the WinUI client can start.
# Does not overwrite existing runtime JSON. Does not download llama.cpp or GGUF unless asked.
[CmdletBinding()]
param(
    [switch]$DownloadChatModel
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

$chatGgufName = 'Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf'
$chatGguf = Join-Path $root "models\$chatGgufName"
$chatUrl = 'https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive/resolve/main/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf'
$chatSha = '2CA636D9E81D3D23CA9B60C234FE185D30EC082EEBA69CE770FDB0C76559A4F5'
$chatSize = [long]5627044224

function Copy-ExampleIfMissing([string]$exampleName, [string]$destName) {
    $example = Join-Path $root "runtime\$exampleName"
    $dest = Join-Path $root "runtime\$destName"
    if (-not (Test-Path -LiteralPath $example)) {
        throw "Missing example config: $example"
    }
    if (Test-Path -LiteralPath $dest) {
        Write-Output "keep $destName"
        return
    }
    Copy-Item -LiteralPath $example -Destination $dest
    Write-Output "wrote $destName"
}

New-Item -ItemType Directory -Force -Path @(
    (Join-Path $root 'llama\bin'),
    (Join-Path $root 'models'),
    (Join-Path $root 'models\sakura-galtransl-7b-v3.7'),
    (Join-Path $root 'runtime\locks'),
    (Join-Path $root 'runtime\logs'),
    (Join-Path $root 'runtime\workflows')
) | Out-Null

Copy-ExampleIfMissing 'model-service.example.json' 'model-service.json'
Copy-ExampleIfMissing 'model-profiles.example.json' 'model-profiles.json'
Copy-ExampleIfMissing 'video-model-profiles.example.json' 'video-model-profiles.json'

$workflowExample = Join-Path $root 'runtime\workflows\minimax-h3-api.json'
if (-not (Test-Path -LiteralPath $workflowExample)) {
    Write-Warning "video workflow missing: runtime\workflows\minimax-h3-api.json (chat still works)"
}

if ($DownloadChatModel) {
    $downloader = Join-Path $root 'scripts\download-segmented.ps1'
    powershell -NoProfile -ExecutionPolicy Bypass -File $downloader `
        -Url $chatUrl `
        -Destination $chatGguf `
        -ExpectedSize $chatSize `
        -ExpectedSha256 $chatSha `
        -AllowedRoot (Join-Path $root 'models')
}

$llama = Join-Path $root 'llama\bin\llama-server.exe'
Write-Output ''
Write-Output 'checklist:'
if (Test-Path -LiteralPath $llama) {
    Write-Output "  llama-server: $llama"
} else {
    Write-Output '  llama-server: MISSING — unzip ggml-org/llama.cpp Windows x64 CUDA 12 zip into llama\bin'
    Write-Output '    https://github.com/ggml-org/llama.cpp/releases'
}
if (Test-Path -LiteralPath $chatGguf) {
    $hash = (Get-FileHash -LiteralPath $chatGguf -Algorithm SHA256).Hash
    if ($hash -eq $chatSha) {
        Write-Output "  chat GGUF: ok $chatGguf"
    } else {
        Write-Output "  chat GGUF: HASH MISMATCH $hash"
    }
} else {
    Write-Output "  chat GGUF: MISSING — $chatGguf"
    Write-Output "    re-run with -DownloadChatModel, or curl the Hugging Face file listed in docs/deploy-local-ai.md"
}

$fill = Join-Path $root 'models\sakura-galtransl-7b-v3.7\Sakura-Galtransl-7B-v3.7.gguf'
if (Test-Path -LiteralPath $fill) {
    Write-Output "  fill GGUF: $fill"
} else {
    Write-Output '  fill GGUF: optional (only for 汉化填字)'
}

Write-Output ''
Write-Output 'next: docs/deploy-local-ai.md  (build WinUI after llama-server and chat GGUF are in place)'
Write-Output 'set NO_PROXY=127.0.0.1,localhost if a system HTTP proxy is on'
