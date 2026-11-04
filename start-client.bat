@echo off
cd /d "%~dp0"
set MIKUSB_OFFLINE=1
if not exist "MikuSB\bin\Debug\net10.0\ClientLauncher.dll" (
  dotnet build tools\ClientLauncher\ClientLauncher.csproj
  if errorlevel 1 (
    pause
    exit /b 1
  )
)
dotnet MikuSB\bin\Debug\net10.0\ClientLauncher.dll
if errorlevel 1 pause
