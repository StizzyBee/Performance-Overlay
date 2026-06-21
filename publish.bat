@echo off
REM Builds the standalone single-file PerformanceOverlay.exe (no .NET install needed on the
REM target PC). Make sure the native hooks are built first: native\build.bat
cd /d "%~dp0"
dotnet publish PerformanceOverlay.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
copy /Y "bin\Release\net10.0-windows\win-x64\publish\PerformanceOverlay.exe" "PerformanceOverlay.exe"
echo.
echo Done -> PerformanceOverlay.exe  (copy this single file to any Windows 10/11 x64 PC)
