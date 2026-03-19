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
    -p:IncludeNativeLibrariesForSelfExtract=false ^
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
echo  The libvlc\ folder alongside Noctis.exe contains the
echo  native audio engine. Both must be included when
echo  distributing (e.g. zip the entire publish\win-x64\ folder).
echo.
pause
