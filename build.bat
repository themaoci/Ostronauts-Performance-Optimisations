@echo off
call "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\Tools\VsDevCmd.bat"
dotnet build -c Release "%~dp0OstronautsPerfOpt.csproj"
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded. DLL deployed to BepInEx\plugins.
) else (
    echo Build failed.
)
pause
