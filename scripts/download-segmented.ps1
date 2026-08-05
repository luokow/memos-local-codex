param(
  [Parameter(Mandatory = $true)][string]$Url,
  [Parameter(Mandatory = $true)][string]$Destination,
  [Parameter(Mandatory = $true)][long]$ExpectedSize,
  [Parameter(Mandatory = $true)][string]$ExpectedSha256,
  [Parameter(Mandatory = $true)][string]$AllowedRoot,
  [ValidateRange(1, 32)][int]$Segments = 12,
  [ValidateRange(0, 30)][int]$StartDelaySec = 0
)

$ErrorActionPreference = 'Stop'
$destinationFull = [IO.Path]::GetFullPath($Destination)
$allowedRootFull = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
if (-not $destinationFull.StartsWith($allowedRootFull, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Destination is outside the allowed root: $destinationFull"
}

if (Test-Path -LiteralPath $destinationFull) {
  $existing = Get-Item -LiteralPath $destinationFull
  if ($existing.Length -eq $ExpectedSize) {
    $existingHash = (Get-FileHash -LiteralPath $destinationFull -Algorithm SHA256).Hash
    if ($existingHash -eq $ExpectedSha256.ToUpperInvariant()) {
      Write-Output "Already verified: $destinationFull"
      exit 0
    }
  }
}

$partDir = "$destinationFull.parts"
$assembling = "$destinationFull.assembling"
New-Item -ItemType Directory -Force -Path $partDir | Out-Null

$segmentSize = [long][Math]::Ceiling($ExpectedSize / [double]$Segments)
$specs = @()
for ($index = 0; $index -lt $Segments; $index++) {
  $start = [long]($index * $segmentSize)
  if ($start -ge $ExpectedSize) { break }
  $end = [long][Math]::Min($ExpectedSize - 1, $start + $segmentSize - 1)
  $partPath = Join-Path $partDir ('part-{0:D3}' -f $index)
  $specs += [pscustomobject]@{
    Index = $index
    Start = $start
    End = $end
    Expected = $end - $start + 1
    Path = $partPath
  }
}

$jobs = @()
foreach ($spec in $specs) {
  $existingPart = Get-Item -LiteralPath $spec.Path -ErrorAction SilentlyContinue
  if ($existingPart -and $existingPart.Length -eq $spec.Expected) { continue }
  $existingBytes = if ($existingPart) { [long]$existingPart.Length } else { 0 }
  if ($existingBytes -gt $spec.Expected) {
    throw "Segment $($spec.Index) is larger than expected; refusing to resume"
  }
  $downloadStart = [long]($spec.Start + $existingBytes)
  $resumePath = "$($spec.Path).resume"
  if (Test-Path -LiteralPath $resumePath) {
    Remove-Item -LiteralPath $resumePath -Force
  }
  $jobs += Start-Job -ArgumentList $Url,$downloadStart,$spec.End,$resumePath -ScriptBlock {
    param($downloadUrl,$rangeStart,$rangeEnd,$outputPath)
    curl.exe --fail --location --silent --show-error --retry 5 --retry-all-errors `
      --connect-timeout 30 `
      --range "$rangeStart-$rangeEnd" --output $outputPath $downloadUrl
    if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE" }
  }
  if ($StartDelaySec -gt 0) { Start-Sleep -Seconds $StartDelaySec }
}

try {
  $lastPercent = -1
  while (@($jobs | Where-Object State -eq 'Running').Count -gt 0) {
    $running = @($jobs | Where-Object State -eq 'Running')
    Wait-Job -Job $running -Any -Timeout 15 | Out-Null
    $downloaded = (Get-ChildItem -LiteralPath $partDir -File -ErrorAction SilentlyContinue |
      Measure-Object -Property Length -Sum).Sum
    if ($null -eq $downloaded) { $downloaded = 0 }
    $percent = [Math]::Round(100 * $downloaded / $ExpectedSize, 1)
    if ($percent -ne $lastPercent) {
      Write-Output "Progress: $percent% ($([Math]::Round($downloaded / 1MB, 1)) MB)"
      $lastPercent = $percent
    }
  }

  $failed = $jobs | Where-Object State -ne 'Completed'
  foreach ($job in $jobs) { Receive-Job -Job $job -ErrorAction Continue }
  if ($failed) {
    throw "One or more segment jobs failed: $($failed.Id -join ', ')"
  }
} finally {
  if ($jobs) { Remove-Job -Job $jobs -Force -ErrorAction SilentlyContinue }
}

foreach ($spec in $specs) {
  $resumePath = "$($spec.Path).resume"
  if (Test-Path -LiteralPath $resumePath) {
    $output = [IO.File]::Open($spec.Path, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
      $input = [IO.File]::OpenRead($resumePath)
      try { $input.CopyTo($output) } finally { $input.Dispose() }
    } finally {
      $output.Dispose()
    }
    Remove-Item -LiteralPath $resumePath -Force
  }
  $part = Get-Item -LiteralPath $spec.Path -ErrorAction Stop
  if ($part.Length -ne $spec.Expected) {
    throw "Segment $($spec.Index) has $($part.Length) bytes; expected $($spec.Expected)"
  }
}

if (Test-Path -LiteralPath $assembling) {
  Remove-Item -LiteralPath $assembling -Force
}
$output = [IO.File]::Open($assembling, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
  foreach ($spec in ($specs | Sort-Object Index)) {
    $input = [IO.File]::OpenRead($spec.Path)
    try { $input.CopyTo($output) } finally { $input.Dispose() }
  }
} finally {
  $output.Dispose()
}

$assembled = Get-Item -LiteralPath $assembling
if ($assembled.Length -ne $ExpectedSize) {
  throw "Assembled file has $($assembled.Length) bytes; expected $ExpectedSize"
}
$hash = (Get-FileHash -LiteralPath $assembling -Algorithm SHA256).Hash
if ($hash -ne $ExpectedSha256.ToUpperInvariant()) {
  throw "SHA-256 mismatch: got $hash, expected $ExpectedSha256"
}

Move-Item -LiteralPath $assembling -Destination $destinationFull -Force
Remove-Item -LiteralPath $partDir -Recurse -Force
Write-Output "Verified SHA-256 $hash"
Write-Output "Saved $destinationFull"
