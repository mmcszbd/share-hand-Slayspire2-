param([string]$OutDir = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'SharedHandPrototype.json') | ConvertFrom-Json
$files = @('SharedHandPrototype.dll', 'SharedHandPrototype.json',
    'SharedHandPrototype.BaseLibUi.dll', 'SharedHandPrototype.RitsuUi.dll', 'defaults.json',
    'layout-background.png', 'layout-card-0.png', 'layout-card-1.png', 'layout-card-2.png', 'layout-card-3.png')
$pngSignature = '89504E470D0A1A0A'
foreach ($name in $files) {
    $path = Join-Path $PSScriptRoot $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty runtime file: $name"
    }
    if ($name.EndsWith('.png')) {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            $header = [byte[]]::new(8)
            $read = $stream.Read($header, 0, 8)
            if ($read -ne 8 -or [Convert]::ToHexString($header) -ne $pngSignature) { throw "Invalid PNG: $name" }
        } finally { $stream.Dispose() }
    }
}
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName((Join-Path $PSScriptRoot 'SharedHandPrototype.dll')).Version
if ($assemblyVersion.ToString(3) -ne $manifest.version) { throw 'Manifest and core assembly version mismatch' }
[System.IO.Directory]::CreateDirectory($OutDir) | Out-Null
$archive = Join-Path $OutDir ("SharedHandPrototype-" + $manifest.version + '-install.zip')
$output = [System.IO.File]::Open($archive, [System.IO.FileMode]::CreateNew)
try {
    $zip = [System.IO.Compression.ZipArchive]::new($output, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($name in $files) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,
                (Join-Path $PSScriptRoot $name), ('SharedHandPrototype/' + $name),
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
} finally { $output.Dispose() }
Write-Output $archive
