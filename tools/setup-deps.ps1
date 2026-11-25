<#
.SYNOPSIS
    Tự động kiểm tra và tải các thành phần native cho PhonePlay Link:
    - Android Platform Tools (adb.exe, AdbWinApi.dll, AdbWinUsbApi.dll)
    - scrcpy-server (Genymobile/scrcpy v5.0.1)
    - FFmpeg 9.0 shared DLLs (BtbN/FFmpeg-Builds, khớp FFmpeg.AutoGen 9.0.1.1)
    - SDL2.dll (libsdl-org/SDL 2.32.10 win64 dự phòng nếu cần)
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipSdlBackup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path -Parent $PSScriptRoot
$AssetsDir = Join-Path $RepoRoot "assets"
$AdbDir = Join-Path $AssetsDir "adb"
$ScrcpyDir = Join-Path $AssetsDir "scrcpy"
$FfmpegDir = Join-Path $AssetsDir "ffmpeg"
$SdlDir = Join-Path $AssetsDir "sdl2"
$TempDir = Join-Path $AssetsDir "_tmp"

$ScrcpyVersion = "v5.0.1"
$ScrcpyServerUrl = "https://github.com/Genymobile/scrcpy/releases/download/$ScrcpyVersion/scrcpy-server-$ScrcpyVersion"
$PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip"
$FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip"
$SdlUrl = "https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip"

function Ensure-Directory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Download-FileFast([string]$Url, [string]$OutFile) {
    Write-Host "  -> Đang tải: $Url" -ForegroundColor Cyan
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -Headers @{ "User-Agent" = "PhonePlayLink-Setup/1.0" }
    if (-not (Test-Path -LiteralPath $OutFile) -or (Get-Item -LiteralPath $OutFile).Length -eq 0) {
        throw "Tải file thất bại hoặc file rỗng: $Url"
    }
}

function Expand-ArchiveClean([string]$ZipPath, [string]$DestDir) {
    if (Test-Path -LiteralPath $DestDir) {
        Remove-Item -LiteralPath $DestDir -Recurse -Force
    }
    Ensure-Directory $DestDir
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $DestDir)
}

Ensure-Directory $AssetsDir
Ensure-Directory $AdbDir
Ensure-Directory $ScrcpyDir
Ensure-Directory $FfmpegDir
Ensure-Directory $TempDir

try {
    # 1. Kiểm tra & tải ADB (Android Platform Tools)
    Write-Host "[1/4] Kiểm tra Android Platform Tools (adb.exe)..." -ForegroundColor Yellow
    $adbFiles = @("adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll")
    $adbMissing = $Force -or (@($adbFiles | Where-Object { -not (Test-Path (Join-Path $AdbDir $_)) }).Count -gt 0)
    if ($adbMissing) {
        $ptZip = Join-Path $TempDir "platform-tools.zip"
        $ptExtract = Join-Path $TempDir "platform-tools"
        Download-FileFast $PlatformToolsUrl $ptZip
        Expand-ArchiveClean $ptZip $ptExtract
        $sourcePtDir = Join-Path $ptExtract "platform-tools"
        foreach ($f in $adbFiles) {
            Copy-Item -LiteralPath (Join-Path $sourcePtDir $f) -Destination (Join-Path $AdbDir $f) -Force
        }
        Write-Host "  [OK] Đã cài đặt adb.exe vào $AdbDir" -ForegroundColor Green
    } else {
        Write-Host "  [OK] Đã có sẵn adb.exe tại $AdbDir" -ForegroundColor Green
    }

    # 2. Kiểm tra & tải scrcpy-server
    Write-Host "[2/4] Kiểm tra scrcpy-server ($ScrcpyVersion)..." -ForegroundColor Yellow
    $scrcpyServerPath = Join-Path $ScrcpyDir "scrcpy-server"
    $scrcpyVersionMarker = Join-Path $ScrcpyDir "version.txt"
    $needScrcpy = $Force -or (-not (Test-Path $scrcpyServerPath)) -or (-not (Test-Path $scrcpyVersionMarker)) -or ((Get-Content $scrcpyVersionMarker -Raw).Trim() -ne $ScrcpyVersion)
    if ($needScrcpy) {
        Download-FileFast $ScrcpyServerUrl $scrcpyServerPath
        Set-Content -LiteralPath $scrcpyVersionMarker -Value $ScrcpyVersion -NoNewline
        Write-Host "  [OK] Đã tải scrcpy-server $ScrcpyVersion vào $scrcpyServerPath" -ForegroundColor Green
    } else {
        Write-Host "  [OK] Đã có sẵn scrcpy-server ($ScrcpyVersion)" -ForegroundColor Green
    }

    # 3. Kiểm tra & tải FFmpeg 9.0 shared DLLs (khớp FFmpeg.AutoGen 9.0.1.1)
    Write-Host "[3/4] Kiểm tra FFmpeg 9.0 shared DLLs..." -ForegroundColor Yellow
    $ffmpegMarker = Join-Path $FfmpegDir "version.txt"
    $existingAvCodec = Get-ChildItem -Path $FfmpegDir -Filter "avcodec-*.dll" -ErrorAction SilentlyContinue
    $needFfmpeg = $Force -or ($null -eq $existingAvCodec) -or (-not (Test-Path $ffmpegMarker)) -or ((Get-Content $ffmpegMarker -Raw).Trim() -ne "9.0")
    if ($needFfmpeg) {
        $ffZip = Join-Path $TempDir "ffmpeg-n9.0.zip"
        $ffExtract = Join-Path $TempDir "ffmpeg"
        Download-FileFast $FfmpegUrl $ffZip
        Expand-ArchiveClean $ffZip $ffExtract
        $binFolder = Get-ChildItem -Path $ffExtract -Directory | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName "bin" }
        if (-not $binFolder -or -not (Test-Path $binFolder)) {
            throw "Không tìm thấy thư mục bin trong archive FFmpeg."
        }
        Get-ChildItem -Path $FfmpegDir -Filter "*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force
        Copy-Item -Path (Join-Path $binFolder "*.dll") -Destination $FfmpegDir -Force
        Set-Content -LiteralPath $ffmpegMarker -Value "9.0" -NoNewline
        $copiedDlls = (Get-ChildItem -Path $FfmpegDir -Filter "*.dll" | Select-Object -ExpandProperty Name) -join ", "
        Write-Host "  [OK] Đã cài FFmpeg 9.0 DLLs: $copiedDlls" -ForegroundColor Green
    } else {
        $existingDlls = (Get-ChildItem -Path $FfmpegDir -Filter "*.dll" | Select-Object -ExpandProperty Name) -join ", "
        Write-Host "  [OK] Đã có sẵn FFmpeg 9.0 DLLs: $existingDlls" -ForegroundColor Green
    }

    # 4. Kiểm tra SDL2.dll dự phòng (Silk.NET.SDL đã kèm Ultz.Native.SDL, nhưng vẫn lưu bản chuẩn dự phòng)
    if (-not $SkipSdlBackup) {
        Write-Host "[4/4] Kiểm tra SDL2.dll (2.32.10)..." -ForegroundColor Yellow
        Ensure-Directory $SdlDir
        $sdlDllPath = Join-Path $SdlDir "SDL2.dll"
        if ($Force -or -not (Test-Path $sdlDllPath)) {
            $sdlZip = Join-Path $TempDir "sdl2.zip"
            $sdlExtract = Join-Path $TempDir "sdl2"
            Download-FileFast $SdlUrl $sdlZip
            Expand-ArchiveClean $sdlZip $sdlExtract
            $foundSdl = Get-ChildItem -Path $sdlExtract -Filter "SDL2.dll" -Recurse | Select-Object -First 1
            if ($null -eq $foundSdl) {
                throw "Không tìm thấy SDL2.dll trong archive."
            }
            Copy-Item -LiteralPath $foundSdl.FullName -Destination $sdlDllPath -Force
            Write-Host "  [OK] Đã cài SDL2.dll vào $sdlDllPath" -ForegroundColor Green
        } else {
            Write-Host "  [OK] Đã có sẵn SDL2.dll tại $sdlDllPath" -ForegroundColor Green
        }
    }

    # Xác minh nhanh adb.exe
    $adbExe = Join-Path $AdbDir "adb.exe"
    $adbVer = & $adbExe version 2>&1 | Select-Object -First 2
    Write-Host "`nKiểm tra chạy thử adb.exe:" -ForegroundColor Cyan
    $adbVer | ForEach-Object { Write-Host "  $_" }

    Write-Host "`nHoàn tất chuẩn bị dependencies cho PhonePlay Link!" -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $TempDir) {
        Remove-Item -LiteralPath $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
