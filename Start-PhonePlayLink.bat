@echo off
setlocal
chcp 65001 >nul
title PhonePlay Link Launcher

set "ROOT_DIR=%~dp0"
set "DIST_DIR=%ROOT_DIR%dist\PhonePlayLink"
set "EXE_PATH=%DIST_DIR%\PhonePlayLink.exe"

if not exist "%EXE_PATH%" (
    echo [PhonePlay Link] Chua thay ban dong goi tai "%DIST_DIR%".
    echo [PhonePlay Link] Dang tu dong build va dong goi Release...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT_DIR%tools\publish-release.ps1"
    if errorlevel 1 (
        echo.
        echo [LOI] Dong goi that bai. Nhan phim bat ky de dong...
        pause >nul
        exit /b 1
    )
)

pushd "%DIST_DIR%"
"%EXE_PATH%" %*
set "APP_EXIT=%ERRORLEVEL%"
popd

if not "%APP_EXIT%"=="0" (
    echo.
    echo [PhonePlay Link] Ung dung da thoat voi ma loi %APP_EXIT%.
    echo Nhan phim bat ky de dong cua so...
    pause >nul
)

exit /b %APP_EXIT%
