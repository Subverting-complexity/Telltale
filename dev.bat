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

:: Start Vite dev server in the foreground
echo Starting Vite dev server...
cd frontend
npm run dev
