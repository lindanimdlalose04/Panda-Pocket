@echo off
REM Panda Pocket: stop everything. Your data is kept.

echo.
echo   Stopping Panda Pocket...
echo.
"C:\Program Files\Docker\Docker\resources\bin\docker.exe" compose --project-directory "%~dp0" stop
echo.
echo   Stopped. Data is kept. Double-click START-HERE.bat to start again.
echo.
pause
