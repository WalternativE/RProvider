@echo off

dotnet tool restore

dotnet paket restore

packages\FAKE\tools\FAKE.exe %* --fsiargs -d:MONO build.fsx 
if not errorlevel 0 goto fakefailed

set exit_code=0
goto leave

:fakefailed
echo command failed: packages\FAKE\tools\FAKE.exe %* --fsiargs -d:MONO build.fsx 
set exit_code=1
goto leave

:leave
exit /b %exit_code%