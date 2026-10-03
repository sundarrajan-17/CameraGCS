@echo off
REM Temporary test script written by Claude: build + unit tests, output to _claude_test.log
cd /d "%~dp0"
set LOG=%~dp0_claude_test.log
echo START %DATE% %TIME% > "%LOG%"
where dotnet >> "%LOG%" 2>&1
dotnet --list-sdks >> "%LOG%" 2>&1
echo === RESTORE === >> "%LOG%"
dotnet restore EpsilonGCS.sln --configfile NuGet.config >> "%LOG%" 2>&1
echo RESTORE_EXIT=%ERRORLEVEL% >> "%LOG%"
echo === BUILD === >> "%LOG%"
dotnet build EpsilonGCS.sln -c Release --no-restore -nologo -clp:NoSummary -v:minimal >> "%LOG%" 2>&1
echo BUILD_EXIT=%ERRORLEVEL% >> "%LOG%"
echo === TEST === >> "%LOG%"
dotnet test tests\Epsilon.Core.Tests\Epsilon.Core.Tests.csproj -c Release --no-build --nologo -v:normal >> "%LOG%" 2>&1
echo TEST_EXIT=%ERRORLEVEL% >> "%LOG%"
echo DONE %DATE% %TIME% >> "%LOG%"
