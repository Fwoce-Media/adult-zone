@echo off
setlocal
title Adult Zone - build and install
cd /d "%~dp0"

rem The program installs here, separate from this source folder. Its library
rem goes in a Data folder beside it, so deleting this source folder to update
rem never touches your library.
set "TARGET=%USERPROFILE%\Documents\Adult Zone"

echo.
echo  Adult Zone - build and install
echo  ------------------------------
echo  Installing to: %TARGET%
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo  The .NET SDK was not found. Install Visual Studio with the
    echo  ".NET desktop development" workload, then run this again.
    echo.
    pause
    exit /b 1
)

tasklist /fi "imagename eq AdultZone.exe" 2>nul | find /i "AdultZone.exe" >nul
if not errorlevel 1 (
    echo  Adult Zone is running. Close it with the power button, then
    echo  run this again.
    echo.
    pause
    exit /b 1
)

echo  Building... this takes a minute or two the first time.
echo.
dotnet publish "src\AdultZone\AdultZone.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true -o "%TARGET%" --nologo -v quiet
if errorlevel 1 (
    echo.
    echo  The build failed. Copy the red lines above and send them over.
    echo.
    pause
    exit /b 1
)

echo.
echo  Creating the desktop shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s = (New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop') + '\Adult Zone.lnk');" ^
  "$s.TargetPath = '%TARGET%\AdultZone.exe';" ^
  "$s.WorkingDirectory = '%TARGET%';" ^
  "$s.IconLocation = '%TARGET%\AdultZone.exe,0';" ^
  "$s.Description = 'Adult Zone';" ^
  "$s.Save()"

echo.
echo  Done.
echo.
echo    Program:   %TARGET%\AdultZone.exe
echo    Library:   %TARGET%\Data
echo    PIN + key: %USERPROFILE%\.adultzone\secrets.json
echo.
echo  The first time it opens, it moves your existing library into the
echo  Data folder. Your PIN and ThePornDB key stay in %USERPROFILE%\.adultzone.
echo.
choice /c YN /n /m "  Open Adult Zone now? [Y/N] "
if errorlevel 2 exit /b 0
start "" "%TARGET%\AdultZone.exe"
exit /b 0
