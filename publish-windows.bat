@echo off
REM ============================================================
REM  Noctis — Windows x64 Publish Script
REM  Produces a self-contained deployment in publish\win-x64\
REM ============================================================

echo.
echo  Building Noctis for Windows x64...
echo.

REM Clean previous output
if exist "publish\win-x64" rmdir /s /q "publish\win-x64"

REM Publish as self-contained for win-x64
dotnet publish src\Noctis\Noctis.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish\win-x64

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo  BUILD FAILED. See errors above.
    pause
    exit /b 1
)

echo.
echo  ============================================================
echo   Build successful!
echo   Output: publish\win-x64\Noctis.exe
echo  ============================================================
echo.
echo  IMPORTANT: The libVLC native DLLs (libvlc/ folder) must
echo  be present alongside Noctis.exe or will be extracted from
echo  the single-file exe to a temp directory on first run.
echo.
echo  If playback fails, copy the libvlc\ folder from:
echo    src\Noctis\bin\Release\net8.0\win-x64\libvlc\
echo  to sit alongside Noctis.exe.
echo.
pause
