@echo off
cd /d "%~dp0"
"%~dp0nrbench.exe" cpu --out "%~dp0cpu-report.txt"
exit /b %errorlevel%
