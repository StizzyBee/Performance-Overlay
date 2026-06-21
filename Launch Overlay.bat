@echo off
REM Launches Performance Overlay as Administrator (needed to read temperatures).
powershell -NoProfile -Command "Start-Process '%~dp0bin\Release\net10.0-windows\PerformanceOverlay.exe' -Verb RunAs"
