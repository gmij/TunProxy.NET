@echo off
setlocal
pushd "%~dp0"

set OUT=%~dp0dist
set RT=win-x64
set CLI=src\TunProxy.CLI\TunProxy.CLI.csproj
set TRAY=src\TunProxy.Tray\TunProxy.Tray.csproj

echo.
echo === TunProxy.NET publish (local) ===
echo Output: %OUT%
echo.

dotnet --version >nul 2>&1
if errorlevel 1 ( echo ERROR: dotnet not found & goto end )

taskkill /f /im TunProxy.Tray.exe >nul 2>&1
taskkill /f /im TunProxy.CLI.exe  >nul 2>&1

if exist "%OUT%" rd /s /q "%OUT%"
mkdir "%OUT%"

echo [1/2] TunProxy.CLI  (single-file + trimmed, AOT=false)...
dotnet publish "%CLI%" -c Release -r %RT% --self-contained ^
  -p:PublishAot=false ^
  -p:PublishSingleFile=true ^
  -p:PublishTrimmed=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o "%OUT%" --nologo
if errorlevel 1 ( echo ERROR: CLI publish failed & goto end )
echo   OK

echo.
echo [2/2] TunProxy.Tray (single-file + trimmed, AOT=false)...
dotnet publish "%TRAY%" -c Release -r %RT% --self-contained ^
  -p:PublishAot=false ^
  -p:PublishSingleFile=true ^
  -p:PublishTrimmed=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:DebugType=none -p:DebugSymbols=false ^
  -o "%OUT%" --nologo
if errorlevel 1 ( echo ERROR: Tray publish failed & goto end )
echo   OK

if exist "%~dp0wintun.dll"    copy /y "%~dp0wintun.dll"    "%OUT%\wintun.dll"    >nul
if exist "%~dp0tunproxy.json" if not exist "%OUT%\tunproxy.json" copy /y "%~dp0tunproxy.json" "%OUT%\tunproxy.json" >nul

del /q "%OUT%\*.pdb"                            2>nul
del /q "%OUT%\*.deps.json"                      2>nul
del /q "%OUT%\*.runtimeconfig.json"             2>nul
del /q "%OUT%\*.staticwebassets.endpoints.json" 2>nul
del /q "%OUT%\web.config"                       2>nul

echo.
echo === Done ===
dir /b "%OUT%"
echo.

:end
popd
endlocal
exit /b
