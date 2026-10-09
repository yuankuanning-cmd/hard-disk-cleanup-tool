@echo off
setlocal

set "LOCAL_DOTNET=%LOCALAPPDATA%\Codex\dotnet-sdk-10\dotnet.exe"
if not exist "%LOCAL_DOTNET%" set "LOCAL_DOTNET=dotnet"

"%LOCAL_DOTNET%" publish "%~dp0src\ChatGPTCleanupTool\ChatGPTCleanupTool.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -o "%~dp0outputs"

if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

echo.
echo Built: %~dp0outputs\DiskCleanupAssistant.exe
if not exist "D:\SoftWare" mkdir "D:\SoftWare"
copy /y "%~dp0outputs\DiskCleanupAssistant.exe" "D:\SoftWare\DiskCleanupAssistant.exe" >nul
if errorlevel 1 exit /b 1
echo Updated: D:\SoftWare\DiskCleanupAssistant.exe
