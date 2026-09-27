@echo off
setlocal
cd /d "%~dp0"

where py >nul 2>nul
if errorlevel 1 (
    set "PYTHON=python"
) else (
    set "PYTHON=py -3"
)

echo Starting the JP AscNet bridge.
echo Leave this window open, wait for the ready message, then launch the game from Steam.
%PYTHON% "%~dp0run_steam.py" --region jp --with-mongo --proxy-local --proxy-log "" --tcp-capture
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo Bridge stopped with exit code %EXIT_CODE%.
pause
exit /b %EXIT_CODE%
