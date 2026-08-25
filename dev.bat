@echo off
echo Starting Telltale development environment...

cd /d "%~dp0"

:: Install frontend dependencies if needed
if not exist frontend\node_modules (
    echo Installing frontend dependencies...
    cd frontend
    npm install
    cd ..
)

:: Start collector in a new window (pause on failure so the error stays visible)
echo Starting collector...
start "Telltale Collector" cmd /c "cd collector && dotnet run || pause"

:: Start viewer backend in a new window
echo Starting viewer backend...
start "Telltale Viewer" cmd /c "cd viewer && dotnet run --launch-profile Development || pause"

:: Wait for backend to start
timeout /t 3 /nobreak >nul

:: Open browser in a standalone app window so it doesn't mix with normal tabs
start "" msedge --app=http://localhost:5173

:: Start Vite dev server in the foreground
echo Starting Vite dev server...
cd frontend
npm run dev
