@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 goto missing_sdk
dotnet publish "StateMachine\Alcyone.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o "obj\one-exe"
if errorlevel 1 goto failed
if not exist "release-exe" mkdir "release-exe"
copy /y "obj\one-exe\Alcyone.exe" "release-exe\Alcyone.exe" >nul
if errorlevel 1 goto failed
echo.
echo EXE ready: %~dp0release-exe\Alcyone.exe
goto done
:missing_sdk
echo Install the .NET 10 SDK on this build computer first.
goto failed
:failed
echo Packaging failed. See the error above.
pause
exit /b 1
:done
pause
exit /b 0
