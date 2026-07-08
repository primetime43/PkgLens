@echo off
rem Double-click this to build and launch the PkgLens GUI. Works from any folder.
setlocal
cd /d "%~dp0"

echo Building PkgLens GUI (Release)...
dotnet build src\PkgLens.Gui\PkgLens.Gui.csproj -c Release -v quiet --nologo
if errorlevel 1 (
  echo.
  echo Build failed. See the output above.
  pause
  exit /b 1
)

start "" "%~dp0src\PkgLens.Gui\bin\Release\net8.0\pkglens.gui.exe"
endlocal
