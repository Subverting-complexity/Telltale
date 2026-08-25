@echo off
echo Starting Telltale development environment...

cd /d "%~dp0"

:: Install frontend dependencies if needed
if not exist frontend\node_modules (
    echo Installing frontend dependencies...
    pushd frontend
    npm install
    popd
)

:: Telltale.exe holds the same recorder lock as the collector below, because both
:: write to the same database, so it is stopped first.
::
:: Forced rather than asked, because asking needs the path to Telltale.exe so it
:: can be run with --quit, and this script never builds it. The cost of forcing is
:: at most one sampling interval of data, which is a fair price for starting a
:: development session. publish.bat, which does know the path, asks first.
tasklist /fi "IMAGENAME eq Telltale.exe" /nh 2>nul | find /i "Telltale.exe" >nul
if not errorlevel 1 (
    echo Stopping Telltale so the development recorder can take over...
    set "TELLTALE_WAS_RUNNING=1"
    taskkill /f /im Telltale.exe >nul 2>&1
    ping -n 4 -w 1000 127.0.0.1 >nul 2>&1
    tasklist /fi "IMAGENAME eq Telltale.exe" /nh 2>nul | find /i "Telltale.exe" >nul
    if not errorlevel 1 (
        echo Could not stop Telltale. Exit it from the notification area and run this again.
        pause
        exit /b 1
    )
)

:: Start collector and viewer in their own windows. cmd /k keeps the window
:: open after the process exits so error output is always readable.
echo Starting collector...
start "Telltale Collector" cmd /k "cd collector && dotnet run"

echo Starting viewer backend...
start "Telltale Viewer" cmd /k "cd viewer && dotnet run --launch-profile Development"

:: Wait for backend to start
timeout /t 3 /nobreak >nul

:: Open browser in a standalone app window so it doesn't mix with normal tabs
start "" msedge --app=http://localhost:5173

:: Start Vite dev server in the foreground. If it exits with an error, pause
:: so the output stays readable in the same way cmd /k keeps the backend
:: windows open.
echo Starting Vite dev server...
cd frontend
npm run dev
if defined TELLTALE_WAS_RUNNING (
    echo.
    echo Telltale was stopped so this session could record. Start it again from
    echo your Startup folder or the deploy target when you are done.
)
if %errorlevel% neq 0 (
    echo.
    echo Vite dev server exited with an error.
    pause
)
