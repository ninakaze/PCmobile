<#
.SYNOPSIS
    Đóng gói PhonePlay Link bản Release (win-x64) sẵn sàng chạy trực tiếp tại dist\PhonePlayLink\.
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SkipSetupDeps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot "src\PhonePlayLink\PhonePlayLink.csproj"
$DistDir = Join-Path $RepoRoot "dist\PhonePlayLink"
$AssetsSrc = Join-Path $RepoRoot "assets"
$ProfilesSrc = Join-Path $RepoRoot "profiles"

if (-not $SkipSetupDeps) {
    Write-Host "[1/4] Kiểm tra dependencies (adb, scrcpy-server, FFmpeg, SDL2)..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "setup-deps.ps1")
}

Write-Host "[2/4] Dọn thư mục xuất bản: $DistDir" -ForegroundColor Yellow
$DistAdb = Join-Path $DistDir "assets\adb\adb.exe"
if (Test-Path -LiteralPath $DistAdb) {
    try { & $DistAdb kill-server 2>$null | Out-Null } catch { }
}
Get-Process -Name "PhonePlayLink", "adb" -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($DistDir, [System.StringComparison]::OrdinalIgnoreCase)
} | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 200

if (Test-Path -LiteralPath $DistDir) {
    Remove-Item -LiteralPath $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DistDir -Force | Out-Null

Write-Host "[3/4] Biên dịch bản Release (win-x64)..." -ForegroundColor Yellow
$scFlag = if ($SelfContained) { "true" } else { "false" }
dotnet publish $CsprojPath `
    -c Release `
    -r win-x64 `
    --self-contained $scFlag `
    -o $DistDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish thất bại với mã lỗi $LASTEXITCODE"
}

Write-Host "[4/4] Sao chép assets (adb, scrcpy-server, ffmpeg) và profiles..." -ForegroundColor Yellow
$AssetsDst = Join-Path $DistDir "assets"
New-Item -ItemType Directory -Path $AssetsDst -Force | Out-Null
foreach ($sub in @("adb", "scrcpy", "ffmpeg", "sdl2")) {
    $srcSub = Join-Path $AssetsSrc $sub
    if (Test-Path -LiteralPath $srcSub) {
        Copy-Item -LiteralPath $srcSub -Destination (Join-Path $AssetsDst $sub) -Recurse -Force
    }
}

$ProfilesDst = Join-Path $DistDir "profiles"
if (Test-Path -LiteralPath $ProfilesSrc) {
    Copy-Item -LiteralPath $ProfilesSrc -Destination $ProfilesDst -Recurse -Force
}

$ExePath = Join-Path $DistDir "PhonePlayLink.exe"
if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Không tìm thấy file thực thi sau publish: $ExePath"
}

Write-Host "`nĐóng gói Release thành công!" -ForegroundColor Green
Write-Host "Thư mục phân phối: $DistDir" -ForegroundColor Cyan
Write-Host "Chạy trực tiếp bằng cách mở: $ExePath" -ForegroundColor Cyan
