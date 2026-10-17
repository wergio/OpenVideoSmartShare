# Compiles installer\OpenVideoSmartShare.iss into dist\ from the exe already published there
# (tools\publish.ps1). The installer takes its version from the exe.
# Needs Inno Setup 7: ISCC.exe from the ISCC environment variable, the PATH or a default
# install folder.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

$candidates = @(
    $env:ISCC,
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe')
)
$iscc = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found: install Inno Setup 7 or set the ISCC environment variable" }

& $iscc /Q "/DDistDir=$dist" "/O$dist" (Join-Path $root 'installer\OpenVideoSmartShare.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler failed" }
Get-ChildItem $dist -Filter 'OpenVideoSmartShare-Setup-*.exe' | ForEach-Object { Write-Output "Installer: $($_.FullName)" }
