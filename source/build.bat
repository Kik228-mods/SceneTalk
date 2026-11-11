@echo off
cd /d "%~dp0"
echo Build started > build.log
set "DN=dotnet"
where dotnet >nul 2>&1 || set "DN=%ProgramFiles%\dotnet\dotnet.exe"
"%DN%" --version >> build.log 2>&1
if exist out\SceneTalk.dll del /q out\SceneTalk.dll
"%DN%" build SceneTalk.csproj -c Release -o out >> build.log 2>&1
if exist out\SceneTalk.dll (
  copy /y out\SceneTalk.dll "..\Mods\SceneTalk.dll" >> build.log 2>&1
  echo RESULT: SUCCESS >> build.log
) else (
  echo RESULT: FAILED >> build.log
)
echo.
findstr /i "RESULT error" build.log
echo.
echo Done. You can close this window.
pause
