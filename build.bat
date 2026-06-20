@echo off
rem Tries common Visual Studio installations, then falls back to plain dotnet.
if exist "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat" (
    call "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat"
) else if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat" (
    call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat"
)
dotnet build -c Release "%~dp0OstronautsPerfOpt.csproj"
if %ERRORLEVEL% EQU 0 (
    echo Build succeeded. DLL deployed to BepInEx\plugins.
) else (
    echo Build failed.
)
pause
