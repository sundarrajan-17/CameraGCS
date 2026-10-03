@echo off
setlocal
REM ==========================================================================
REM  Epsilon GCS - one-step build.  Produces:
REM     release\EpsilonGCS\EpsilonGCS.exe      (self-contained, no .NET install needed to run)
REM     release\Simulator\EpsilonSimulator.exe (gimbal simulator for desk testing)
REM  Requirements: Windows 10/11 x64, .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0), internet for NuGet.
REM ==========================================================================
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] .NET SDK not found. Install the .NET 8 SDK x64 and run build.bat again.
  echo         https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

echo.
echo === 1/5  Restore ===
dotnet restore EpsilonGCS.sln --configfile NuGet.config || goto :fail

echo.
echo === 2/5  Unit tests (protocol / checksum) ===
dotnet test tests\Epsilon.Core.Tests\Epsilon.Core.Tests.csproj -c Release --nologo || goto :fail

echo.
echo === 3/5  Publish Epsilon GCS ===
REM The release folder cannot be replaced while programs from it are still running.
set RUNNING=
for %%P in (EpsilonGCS.exe EpsilonSimulator.exe mediamtx.exe) do (
  tasklist /FI "IMAGENAME eq %%P" 2>nul | find /I "%%P" >nul && set RUNNING=1 && echo [ERROR] %%P is still running.
)
if defined RUNNING (
  echo         Close Epsilon GCS and the simulator window, then run build.bat again.
  pause
  exit /b 1
)
if exist release rmdir /s /q release
if exist release (
  echo [ERROR] Could not delete the old release folder - a file in it is still in use
  echo         ^(usually ffmpeg.exe started by the simulator or the RTSP restream^). Close it and try again.
  pause
  exit /b 1
)
dotnet publish src\EpsilonGCS\EpsilonGCS.csproj -c Release -r win-x64 --self-contained true -o release\EpsilonGCS --nologo || goto :fail

echo.
echo === 4/5  Publish simulator ===
dotnet publish src\Epsilon.Simulator\Epsilon.Simulator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o release\Simulator --nologo || goto :fail

echo.
echo === 5/5  RTSP tools (ffmpeg + mediamtx) ===
if not exist tools\ffmpeg.exe (
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\get-tools.ps1
  if errorlevel 1 echo [WARN] Could not download tools. RTSP restream needs tools\ffmpeg.exe and tools\mediamtx.exe.
)
if not exist release\EpsilonGCS\tools mkdir release\EpsilonGCS\tools
if exist tools\ffmpeg.exe copy /y tools\ffmpeg.exe release\EpsilonGCS\tools\ >nul
if exist tools\mediamtx.exe copy /y tools\mediamtx.exe release\EpsilonGCS\tools\ >nul
copy /y tools\get-tools.ps1 release\EpsilonGCS\tools\ >nul

> release\run-simulator-with-video.bat echo @echo off
>> release\run-simulator-with-video.bat echo cd /d "%%~dp0Simulator"
>> release\run-simulator-with-video.bat echo EpsilonSimulator.exe --video
>> release\run-simulator-with-video.bat echo pause

echo.
echo ==========================================================================
echo  BUILD OK
echo    App:        release\EpsilonGCS\EpsilonGCS.exe
echo    Simulator:  release\run-simulator-with-video.bat
echo ==========================================================================
pause
exit /b 0

:fail
echo.
echo [ERROR] Build failed - see the messages above.
pause
exit /b 1
