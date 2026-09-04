[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$productRoot = Split-Path -Parent $root
$payload = [IO.Path]::GetFullPath($PayloadPath)
$output = [IO.Path]::GetFullPath($OutputPath)
$assetManifestPath = Join-Path $productRoot 'third_party\BUNDLED-ASSETS.json'
$versionPropsPath = Join-Path $productRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw 'Payload is missing.' }
if ((Get-Item -LiteralPath $payload).Length -lt 50000000) { throw 'Payload is too small to contain the self-contained WPF runtime.' }
if (-not $output.StartsWith(([IO.Path]::GetFullPath($productRoot).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must stay inside the SpeechRibbon root.' }
if (-not (Test-Path -LiteralPath $assetManifestPath -PathType Leaf)) { throw 'Bundled asset manifest is missing.' }
$assetManifest = Get-Content -LiteralPath $assetManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$assetManifest.schemaVersion -ne 1 -or @($assetManifest.assets).Count -ne 8) { throw 'Bundled asset manifest is invalid.' }
[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw -Encoding UTF8
$version = [string]$versionProps.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Directory.Build.props must contain a three-part numeric Version.' }
$versionCommas = $version.Replace('.', ',')

$toolRoot = Join-Path $productRoot 'tools\w64devkit-2.9.1\w64devkit\bin'
$gcc = Join-Path $toolRoot 'gcc.exe'
$windres = Join-Path $toolRoot 'windres.exe'
foreach ($tool in @($gcc, $windres)) { if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Required compiler is missing: $tool" } }

$objectRoot = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force -Path $objectRoot | Out-Null
$resourceObject = Join-Path $objectRoot 'SpeechRibbonLauncher.res.o'
$launcher = Join-Path $objectRoot 'SpeechRibbonLauncher.exe'
$generatedResource = Join-Path $objectRoot 'SpeechRibbonLauncher.generated.rc'
$generatedSource = Join-Path $objectRoot 'SpeechRibbonLauncher.generated.c'
$resourceTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SpeechRibbonLauncher.rc') -Raw -Encoding UTF8
$sourceTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SpeechRibbonLauncher.c') -Raw -Encoding UTF8
[IO.File]::WriteAllText($generatedResource, $resourceTemplate.Replace('__SPEECHRIBBON_VERSION_COMMAS__', $versionCommas).Replace('__SPEECHRIBBON_VERSION__', $version), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($generatedSource, $sourceTemplate.Replace('__SPEECHRIBBON_VERSION__', $version), [Text.UTF8Encoding]::new($false))
& $windres $generatedResource -I (Join-Path $productRoot 'src\SpeechRibbon\Assets') -O coff -o $resourceObject
if ($LASTEXITCODE -ne 0) { throw 'windres failed.' }
& $gcc ("-B" + $toolRoot + '\') $generatedSource $resourceObject -municode -mwindows -Os -s -static -lole32 -lshell32 -lbcrypt -o $launcher
if ($LASTEXITCODE -ne 0) { throw 'gcc failed.' }

$temporaryOutput = "$output.tmp"
if (Test-Path -LiteralPath $temporaryOutput) { Remove-Item -LiteralPath $temporaryOutput -Force }
$launcherStream = [IO.File]::OpenRead($launcher)
$payloadStream = [IO.File]::OpenRead($payload)
$destination = [IO.File]::Open($temporaryOutput, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $launcherStream.CopyTo($destination)
    $assetPaths = @(
        (Join-Path $productRoot 'third_party\bundled\whisper-bin-x64-v1.9.2.zip'),
        (Join-Path $productRoot 'third_party\bundled\ggml-small-q8_0.bin'),
        (Join-Path $productRoot 'third_party\bundled\ggml-silero-v6.2.0.bin'),
        (Join-Path $productRoot 'third_party\bundled\ffmpeg-9.0.1-speechribbon-decoder.zip'),
        (Join-Path $productRoot 'third_party\bundled\bergamot-enru.zip'),
        (Join-Path $productRoot 'third_party\bundled\bergamot-jaen.zip'),
        (Join-Path $productRoot 'third_party\bundled\third-party-sources.zip'),
        (Join-Path $productRoot 'third_party\THIRD-PARTY-NOTICES.txt')
    )
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($assetPath in $assetPaths) {
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Required bundled asset is missing: $assetPath" }
        $asset = Get-Item -LiteralPath $assetPath
        $declared = @($assetManifest.assets | Where-Object { [string]$_.name -eq $asset.Name })
        $actualHash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash
        if ($declared.Count -ne 1 -or [long]$declared[0].sizeBytes -ne [long]$asset.Length -or [string]$declared[0].sha256 -ne $actualHash) {
            throw "Bundled asset differs from the immutable manifest: $($asset.Name)"
        }
        $entry = [ordered]@{ name = $asset.Name; offset = $destination.Position; length = $asset.Length; sha256 = $actualHash }
        $assetStream = [IO.File]::OpenRead($asset.FullName)
        try { $assetStream.CopyTo($destination) } finally { $assetStream.Dispose() }
        $entries.Add($entry)
    }
    $manifestBytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ schemaVersion = 1; entries = $entries } | ConvertTo-Json -Depth 8 -Compress))
    $manifestHash = [Security.Cryptography.SHA256]::HashData($manifestBytes)
    $assetsMagic = [Text.Encoding]::ASCII.GetBytes('SRASST01')
    $destination.Write($manifestBytes, 0, $manifestBytes.Length)
    $manifestLength = [BitConverter]::GetBytes([uint64]$manifestBytes.Length)
    $destination.Write($manifestLength, 0, $manifestLength.Length)
    $destination.Write($manifestHash, 0, $manifestHash.Length)
    $destination.Write($assetsMagic, 0, $assetsMagic.Length)
    $payloadStream.CopyTo($destination)
    $payloadLength = [BitConverter]::GetBytes([uint64]$payloadStream.Length)
    $payloadHash = [Convert]::FromHexString((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash)
    $magic = [Text.Encoding]::ASCII.GetBytes('SRBNDL01')
    $destination.Write($payloadLength, 0, $payloadLength.Length)
    $destination.Write($payloadHash, 0, $payloadHash.Length)
    $destination.Write($magic, 0, $magic.Length)
    $destination.Flush($true)
}
finally {
    $destination.Dispose()
    $payloadStream.Dispose()
    $launcherStream.Dispose()
}
[IO.File]::Move($temporaryOutput, $output, $true)
[pscustomobject]@{
    output = $output
    size = (Get-Item -LiteralPath $output).Length
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    payloadSha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    assetManifestSha256 = (Get-FileHash -LiteralPath $assetManifestPath -Algorithm SHA256).Hash
}
