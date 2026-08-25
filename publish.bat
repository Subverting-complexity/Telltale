@echo off
setlocal
set "ROOT=%~dp0"

echo Building Telltale...
echo.

:: Build frontend
echo [1/2] Building frontend...
pushd "%ROOT%frontend"
if not exist node_modules (
    echo       Installing dependencies...
    call npm install
    if errorlevel 1 goto :fail
)
call npm run build
if errorlevel 1 goto :fail
popd

:: Publish the application. One executable: it records in the background and
:: serves its own window. The frontend assets and telltale.json are copied
:: alongside it by the project file.
echo [2/2] Publishing Telltale...
dotnet publish "%ROOT%host\Telltale.csproj" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%ROOT%publish" --nologo -v quiet
if errorlevel 1 goto :fail

echo.
echo Build complete. Output in publish\
echo.
echo   publish\Telltale.exe   Records in the background, serves its own window
echo.
echo Telltale records for as long as it is running and shows an icon in the
echo notification area. Click the icon, or start Telltale again, to open the
echo window.
echo.
echo The window is served on http://127.0.0.1:41821 while it is open, and on a
echo port Windows picks if that one is already taken. Nothing is listening while
echo the window is closed. Change the port with viewerPort in telltale.json.
goto :end

:fail
echo.
echo BUILD FAILED
exit /b 1

:end
endlocal
