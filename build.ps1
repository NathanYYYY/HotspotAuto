$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\HotspotAuto.cs'
$manifest = Join-Path $root 'src\app.manifest'
$icon = Join-Path $root 'src\app.ico'
$outDir = Join-Path $root 'dist'

# Locate the .NET Framework C# compiler (works on 64-bit and 32-bit Windows).
$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw ".NET Framework 4.x csc.exe not found. Install .NET Framework 4.8 and retry."
}

$runtime = Join-Path (Split-Path -Parent $csc) 'System.Runtime.dll'
if (-not (Test-Path $runtime)) { throw "System.Runtime.dll not found next to $csc" }

$winmdCandidates = @(
    (Join-Path $env:WINDIR 'System32\WinMetadata'),
    (Join-Path $env:WINDIR 'SysWOW64\WinMetadata')
)
$winmd = $winmdCandidates | Where-Object {
    (Test-Path (Join-Path $_ 'Windows.Foundation.winmd')) -and
    (Test-Path (Join-Path $_ 'Windows.Networking.winmd'))
} | Select-Object -First 1
if (-not $winmd) { throw "WinMetadata (Windows.Foundation.winmd / Windows.Networking.winmd) not found." }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    "/out:$outDir\HotspotAuto.exe",
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    "/r:$runtime",
    "/r:$winmd\Windows.Foundation.winmd",
    "/r:$winmd\Windows.Networking.winmd",
    "/win32manifest:$manifest"
)
if (Test-Path $icon) { $cscArgs = @("/win32icon:$icon") + $cscArgs }
$cscArgs += $src

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir 'HotspotAuto.exe'
Write-Host "OK: $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
