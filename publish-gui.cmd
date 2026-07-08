@echo off
rem Run once to get a standalone PkgLens.exe and a Desktop shortcut you can pin.
setlocal
cd /d "%~dp0"

echo Publishing standalone PkgLens GUI (this can take a minute)...
dotnet publish src\PkgLens.Gui\PkgLens.Gui.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0dist" -v quiet --nologo
if errorlevel 1 (
  echo.
  echo Publish failed. See the output above.
  pause
  exit /b 1
)

powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\PkgLens.lnk'); $s.TargetPath='%~dp0dist\pkglens.gui.exe'; $s.WorkingDirectory='%~dp0dist'; $s.Save()"

echo.
echo Done. A "PkgLens" shortcut is on your Desktop.
echo You can also pin dist\pkglens.gui.exe to the Start menu or taskbar.
pause
endlocal
