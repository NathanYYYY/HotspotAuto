$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = '1.0.0'
$packageDir = Join-Path $root 'dist\package'
$zipPath = Join-Path $root "dist\HotspotAuto-$version.zip"

& (Join-Path $root 'build.ps1')

if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

$files = @(
    'dist\HotspotAuto.exe',
    'README.md',
    'README.en.md',
    '使用说明.txt',
    'LICENSE',
    '安装开机自启.bat',
    '卸载开机自启.bat',
    '关闭快速启动.bat',
    '恢复快速启动.bat',
    'docs\screenshot.png'
)
foreach ($rel in $files) {
    $from = Join-Path $root $rel
    if (-not (Test-Path $from)) { throw "Missing file: $from" }
    if ($rel -like 'docs\*') {
        $dest = Join-Path $packageDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    } else {
        $dest = Join-Path $packageDir (Split-Path -Leaf $rel)
    }
    Copy-Item $from $dest -Force
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $packageDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zip = Get-Item $zipPath
Write-Host "OK: $zipPath ($([math]::Round($zip.Length / 1KB)) KB)"
