@echo off
rem Rebuild BatteryPill.exe and copy it to %LOCALAPPDATA%\BatteryPill
setlocal
set "CSC=C:\Program Files\dotnet\sdk\6.0.418\Roslyn\bincore\csc.dll"
if not exist "%CSC%" (
  echo Cannot find csc.dll. Install the .NET SDK, or edit CSC in this file.
  exit /b 1
)
pushd "%~dp0src"
dotnet "%CSC%" @build.rsp
set "RC=%errorlevel%"
popd
if not exist "%~dp0BatteryPill.exe" (
  echo Build failed.
  exit /b 1
)

set "DST=%LOCALAPPDATA%\BatteryPill"
if not exist "%DST%" mkdir "%DST%"
copy /Y "%~dp0BatteryPill.exe" "%DST%\BatteryPill.exe" >nul
echo.
echo Build OK, installed to "%DST%\BatteryPill.exe"
echo NOTE: run it from a normal user folder, not elevated: the taskbar belongs to
echo       Explorer (Medium integrity), and a High (admin) or Low integrity process
echo       cannot attach to it - the pill would float above the taskbar instead.
exit /b %RC%
