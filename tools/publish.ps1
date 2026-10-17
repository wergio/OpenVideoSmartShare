# Builds a single-file OpenVideoSmartShare.exe into dist\.
#   -Version 1.2.0   version stamped into the exe (default: the one in the csproj)
#   -Installer       also builds dist\OpenVideoSmartShare-Setup-<version>.exe (needs Inno Setup 7)
#   -Install         copies the exe to %LOCALAPPDATA%\Programs\OpenVideoSmartShare (closing a
#                    running instance first), the same folder the installer uses
# Requires the .NET 10 Desktop and ASP.NET Core runtimes on the target PC.
param(
    [string]$Version,
    [switch]$Installer,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$publishArgs = @(
    (Join-Path $root 'src\OpenVideoSmartShare\OpenVideoSmartShare.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
    '-p:PublishSingleFile=true', '-p:DebugType=none',
    '-o', $dist
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if ($Installer) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1')
}

if ($Install) {
    $target = Join-Path $env:LOCALAPPDATA 'Programs\OpenVideoSmartShare'
    $running = Get-Process OpenVideoSmartShare -ErrorAction SilentlyContinue
    if ($running) {
        # Ask the running instance to quit cleanly (SSDP byebye), then make sure it is gone.
        $exe = Join-Path $target 'OpenVideoSmartShare.exe'
        if (Test-Path $exe) { & $exe --exit }
        foreach ($p in $running) {
            if (-not $p.WaitForExit(8000)) { $p.Kill() }
        }
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    # The executable can stay locked for a moment after the process exits.
    for ($attempt = 1; ; $attempt++) {
        try {
            Copy-Item (Join-Path $dist 'OpenVideoSmartShare.exe') $target -Force -ErrorAction Stop
            break
        }
        catch [System.IO.IOException] {
            if ($attempt -ge 10) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Output "Installed to $target"
}
