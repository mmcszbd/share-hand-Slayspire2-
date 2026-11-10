param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\Slay the Spire 2',
    [string]$RitsuDir = 'D:\SteamLibrary\steamapps\workshop\content\2868840\3747602295',
    [string]$BaseLibDir = 'D:\SteamLibrary\steamapps\workshop\content\2868840\3737335127\BaseLib',
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
# Use the Roslyn compiler bundled with PowerShell 7. No SDK installation or game execution.
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.dll')
Add-Type -Path (Join-Path $PSHOME 'Microsoft.CodeAnalysis.CSharp.dll')
Add-Type -TypeDefinition @'
using System;
using System.IO;
public static class SharedHandResourceFactory {
    public static Func<Stream> Create(string path) {
        byte[] bytes = File.ReadAllBytes(path);
        return () => new MemoryStream(bytes, false);
    }
}
'@
$gameData = Join-Path $GameDir 'data_sts2_windows_x86_64'
$runtimePath = Join-Path $RitsuDir 'compat\0.111.0\STS2-RitsuLib.Runtime.dll'
# RitsuLib is optional.
$referencePaths = [System.Collections.Generic.List[string]]::new()
foreach ($file in Get-ChildItem -LiteralPath $gameData -Filter '*.dll') {
    try { [System.Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null }
    catch { continue }
    $referencePaths.Add($file.FullName)
}
# Optional frontends are built separately; the core references only the game.
# No BaseLib core reference.


function Compile-Sources([string]$Name, [string[]]$Paths, [string]$Destination, [bool]$Executable) {
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    $parseOptions = [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default.WithLanguageVersion(
        [Microsoft.CodeAnalysis.CSharp.LanguageVersion]::Preview)
    foreach ($sourcePath in $Paths) {
        $source = [System.IO.File]::ReadAllText($sourcePath)
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $parseOptions, $sourcePath))
    }
    $references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($referencePath in $referencePaths) {
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($referencePath))
    }
    $kind = if ($Executable) { [Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication }
        else { [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new($kind).
        WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).
        WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable).
        WithDeterministic($true)
    $compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($Name, $trees, $references, $options)
    $stream = [System.IO.File]::Create($Destination)
    try {
        if ($Name -eq 'SharedHandPrototype') {
            $resources = [System.Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
            foreach ($asset in @('layout-background.png','layout-card-0.png','layout-card-1.png','layout-card-2.png','layout-card-3.png')) {
                $assetPath = Join-Path $PSScriptRoot $asset
                $resources.Add([Microsoft.CodeAnalysis.ResourceDescription]::new('SharedHandPrototype.Assets.'+$asset, [SharedHandResourceFactory]::Create($assetPath), $true))
            }
            $result = $compilation.Emit($stream,$null,$null,$null,$resources,$null,[System.Threading.CancellationToken]::None)
        } else { $result = $compilation.Emit($stream) }
    } finally { $stream.Dispose() }
    foreach ($diagnostic in $result.Diagnostics) {
        if ($diagnostic.Severity -eq [Microsoft.CodeAnalysis.DiagnosticSeverity]::Error) {
            Write-Host $diagnostic.ToString()
        }
    }
    if (!$result.Success) { throw "Compilation failed: $Name" }
    Write-Host "Compiled: $Destination"
}

$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
Compile-Sources 'SharedHandPrototype' $sources (Join-Path $PSScriptRoot 'SharedHandPrototype.dll') $false
# Extra compiler references below are for optional adapters and the test fixture.
# They are deliberately added only after the independent core has compiled.
if(Test-Path -LiteralPath $runtimePath){$referencePaths.Add($runtimePath)}
$baseReference=Join-Path $BaseLibDir 'BaseLib.dll'
if(Test-Path -LiteralPath $baseReference){$referencePaths.Add($baseReference)}
$sharedDirectory=Join-Path $RitsuDir 'shared'
if(Test-Path -LiteralPath $sharedDirectory){foreach($extraReference in Get-ChildItem -LiteralPath $sharedDirectory -Filter '*.dll'){$referencePaths.Add($extraReference.FullName)}}
$referencePaths.Add((Join-Path $PSScriptRoot 'SharedHandPrototype.dll'))
if(Test-Path -LiteralPath $baseReference){Compile-Sources 'SharedHandPrototype.BaseLibUi' @((Join-Path $PSScriptRoot 'optional-src/BaseLibUi.cs')) (Join-Path $PSScriptRoot 'SharedHandPrototype.BaseLibUi.dll') $false}
if(Test-Path -LiteralPath (Join-Path $sharedDirectory 'STS2-RitsuLib.Settings.dll')){Compile-Sources 'SharedHandPrototype.RitsuUi' @((Join-Path $PSScriptRoot 'optional-src/RitsuUi.cs')) (Join-Path $PSScriptRoot 'SharedHandPrototype.RitsuUi.dll') $false}
if ($RunTests) {
    $testDir = Join-Path $PSScriptRoot 'tests'
    $testOutput = Join-Path $testDir 'ProtocolTests.dll'
    Compile-Sources 'ProtocolTests' @((Join-Path $PSScriptRoot 'src\Protocol.cs'), (Join-Path $testDir 'ProtocolTests.cs')) $testOutput $true
    '{ "runtimeOptions": { "tfm": "net10.0", "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" } } }' |
        Set-Content -LiteralPath (Join-Path $testDir 'ProtocolTests.runtimeconfig.json') -Encoding utf8
    & dotnet $testOutput
    if ($LASTEXITCODE -ne 0) { throw 'Protocol tests failed.' }
}


