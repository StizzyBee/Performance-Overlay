@echo off
REM Builds the injection natives (x64 + x86). Requires Visual Studio C++ tools.
set "VC=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build"
cd /d "%~dp0"

REM ---- x64 hook DLL ----
setlocal
call "%VC%\vcvars64.bat" >nul
cl /nologo /LD /O2 /MT /EHsc PerfOverlayHook.cpp /link /OUT:PerfOverlayHook.dll /IMPLIB:PerfOverlayHook.lib
endlocal

REM ---- x86 hook DLL + 32-bit injector helper ----
setlocal
call "%VC%\vcvars32.bat" >nul
cl /nologo /LD /O2 /MT /EHsc PerfOverlayHook.cpp /link /OUT:PerfOverlayHook32.dll /IMPLIB:PerfOverlayHook32.lib
cl /nologo /O2 /MT /EHsc Inject32.cpp /Fe:PerfOverlayInject32.exe /link user32.lib shell32.lib
endlocal

echo --- results ---
if exist PerfOverlayHook.dll      (echo x64 DLL OK)    else (echo x64 DLL FAILED)
if exist PerfOverlayHook32.dll    (echo x86 DLL OK)    else (echo x86 DLL FAILED)
if exist PerfOverlayInject32.exe  (echo x86 EXE OK)    else (echo x86 EXE FAILED)
