@echo off
setlocal
set "ROOT=%~dp0"

echo Building Telltale...
echo.

:: Build frontend
echo [1/4] Building frontend...
pushd "%ROOT%frontend"
if not exist node_modules (
    echo       Installing dependencies...
    call npm install
    if errorlevel 1 goto :fail
)
call npm run build
if errorlevel 1 goto :fail
popd

:: Publish collector
echo [2/4] Publishing collector...
dotnet publish "%ROOT%collector\Collector.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%ROOT%publish\collector" --nologo -v quiet
if errorlevel 1 goto :fail
copy /y "%ROOT%telltale.json" "%ROOT%publish\collector\telltale.json" >nul

:: Publish viewer
echo [3/4] Publishing viewer...
dotnet publish "%ROOT%viewer\Viewer.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%ROOT%publish\viewer" --nologo -v quiet
if errorlevel 1 goto :fail

:: Copy frontend assets into viewer output
echo [4/4] Copying frontend assets...
xcopy /s /y /q "%ROOT%viewer\wwwroot\*" "%ROOT%publish\viewer\wwwroot\" >nul

echo.
echo Build complete. Output in publish\
echo.
echo   publish\collector\TelltaleCapture.exe  - Background process recorder
echo   publish\viewer\TelltaleViewer.exe      - Web-based viewer (http://localhost:5111)
echo.
echo To run: start TelltaleCapture.exe first, then open TelltaleViewer.exe.
goto :end

:fail
echo.
echo BUILD FAILED
exit /b 1

:end
endlocal
