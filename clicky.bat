@echo off
setlocal

cd /d "%~dp0"

set "DOTNET=dotnet"
if exist "%CD%\.dotnet\dotnet.exe" set "DOTNET=%CD%\.dotnet\dotnet.exe"

set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=run"

if /i "%ACTION%"=="run" goto run
if /i "%ACTION%"=="build" goto build
if /i "%ACTION%"=="test" goto test
goto usage

:run
echo Building Clicky...
"%DOTNET%" build ".\windows\Clicky.Windows.sln" -c Release
if errorlevel 1 goto failed

echo Launching Clicky...
start "" "%CD%\windows\src\Clicky.Windows\bin\Release\net10.0-windows\Clicky.Windows.exe"
exit /b 0

:build
"%DOTNET%" build ".\windows\Clicky.Windows.sln" -c Release
exit /b %ERRORLEVEL%

:test
"%DOTNET%" test ".\windows\Clicky.Windows.sln" -c Release
exit /b %ERRORLEVEL%

:usage
echo Usage: clicky.bat [run^|build^|test]
exit /b 2

:failed
echo.
echo Clicky could not be built. Make sure the .NET 10 SDK is installed.
pause
exit /b 1
