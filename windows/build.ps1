[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist'),
    [string]$RuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$nodeVersion = 'v24.22.3'
$zipName = "node-$nodeVersion-win-x64.zip"
# https://nodejs.org/en/blog/release/v24.22.3#shasums
$zipHash = 'e612af99f626ab415ea225af6bce5c971feb448cb5cae250bb1c3fa50c5f7d9f'
$nodeHash = '86fc4785327db5f5c7558dc6156c543b433c313e813a222e088086222ab4afa6'
$exeName = 'DuetRhythmPlus-Windows-x64.exe'
$repoDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$stagingParent = Join-Path $repoDirectory 'dist'
New-Item -ItemType Directory -Path $stagingParent -Force | Out-Null
$stagingParent = (Resolve-Path -LiteralPath $stagingParent).ProviderPath
$stagingDirectory = Join-Path $stagingParent ('windows-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
$stagingDirectory = (Resolve-Path -LiteralPath $stagingDirectory).ProviderPath

try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw 'The 64-bit .NET Framework C# compiler is required to build the Windows EXE.'
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (!$RuntimeDirectory) {
        $nodeArchive = Join-Path $stagingDirectory $zipName
        Write-Host "Downloading official Node.js $nodeVersion for Windows x64."
        Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/$nodeVersion/$zipName" -OutFile $nodeArchive
        if ((Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash -ne $zipHash) {
            throw "The Node.js archive does not match the published SHA-256 for $zipName."
        }
        [IO.Compression.ZipFile]::ExtractToDirectory($nodeArchive, $stagingDirectory)
        $RuntimeDirectory = Join-Path $stagingDirectory "node-$nodeVersion-win-x64"
    }

    $nodeExe = Join-Path $RuntimeDirectory 'node.exe'
    $nodeLicense = Join-Path $RuntimeDirectory 'LICENSE'
    if (!(Test-Path -LiteralPath $nodeExe -PathType Leaf) -or !(Test-Path -LiteralPath $nodeLicense -PathType Leaf)) {
        throw "The runtime directory must contain the official Windows x64 node.exe and LICENSE for $nodeVersion."
    }
    if ((Get-FileHash -LiteralPath $nodeExe -Algorithm SHA256).Hash -ne $nodeHash) {
        throw "node.exe does not match the SHA-256 for the official $nodeVersion Windows x64 binary."
    }

    $payloadDirectory = Join-Path $stagingDirectory 'payload'
    $payloadRuntime = Join-Path $payloadDirectory 'runtime'
    $payloadWindows = Join-Path $payloadDirectory 'windows'
    New-Item -ItemType Directory -Path $payloadRuntime, $payloadWindows | Out-Null
    foreach ($entry in 'package.json', 'LICENSE', 'public', 'server', 'drizzle') {
        Copy-Item -LiteralPath (Join-Path $repoDirectory $entry) -Destination $payloadDirectory -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'desktop-server.mjs') -Destination $payloadWindows
    Copy-Item -LiteralPath $nodeExe, $nodeLicense -Destination $payloadRuntime

    $payloadArchive = Join-Path $stagingDirectory 'payload.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payloadDirectory, $payloadArchive)
    $stagedExe = Join-Path $stagingDirectory $exeName
    $compilerArguments = @(
        '/nologo'
        '/target:winexe'
        '/platform:x64'
        '/optimize+'
        '/codepage:65001'
        '/reference:System.Windows.Forms.dll'
        '/reference:System.Drawing.dll'
        '/reference:System.IO.Compression.dll'
        '/reference:System.IO.Compression.FileSystem.dll'
        '/reference:Microsoft.CSharp.dll'
        "/resource:$payloadArchive,DuetRhythmPlus.Payload"
        "/out:$stagedExe"
        (Join-Path $PSScriptRoot 'Launcher.cs')
    )
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "The Windows EXE build failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $outputExe = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).ProviderPath $exeName
    Move-Item -LiteralPath $stagedExe -Destination $outputExe -Force
    Get-Item -LiteralPath $outputExe
} finally {
    if ($stagingDirectory.StartsWith($stagingParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
