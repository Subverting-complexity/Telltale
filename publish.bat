@echo off
echo Building Telltale...

cd /d "%~dp0"

:: Build frontend
echo Building frontend...
cd frontend
if not exist node_modules (
    echo Installing frontend dependencies...
    npm install
)
npm run build
cd ..

:: Publish collector
echo Publishing collector...
dotnet publish collector/Collector.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/collector

:: Copy telltale.json template next to collector
copy telltale.json publish\collector\telltale.json

:: Publish viewer
echo Publishing viewer...
dotnet publish viewer/Viewer.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/viewer

:: Copy wwwroot to viewer output
echo Copying frontend assets...
xcopy /s /y "viewer\wwwroot\*" "publish\viewer\wwwroot\"

echo.
echo Build complete. Output in publish/
echo   publish/collector/Collector.exe  - Background process recorder
echo   publish/viewer/Viewer.exe        - Web-based viewer (http://localhost:5111)
