@echo off
setlocal
rem Drag a .gma file (or an extracted addon folder) onto this script.
rem Set the BLENDER environment variable if Blender is not found automatically.

if "%~1"=="" (
    echo Drag a .gma file onto this script.
    pause
    exit /b 1
)

if not defined BLENDER (
    for %%P in (
        "%ProgramFiles%\Blender Foundation\Blender 5.2\blender.exe"
        "%ProgramFiles%\Blender Foundation\Blender 5.1\blender.exe"
        "%ProgramFiles%\Blender Foundation\Blender 4.5\blender.exe"
        "%ProgramFiles(x86)%\Steam\steamapps\common\Blender\blender.exe"
    ) do if not defined BLENDER if exist %%P set "BLENDER=%%~P"
)
if not defined BLENDER for /f "delims=" %%P in ('where blender 2^>nul') do if not defined BLENDER set "BLENDER=%%P"
if not defined BLENDER (
    echo Blender not found. Set BLENDER to the full path of blender.exe.
    pause
    exit /b 1
)

"%BLENDER%" -b --factory-startup -P "%~dp0blender\gma2blend.py" -- "%~1" "%~dpn1_converted" %2 %3 %4
echo.
echo Output: "%~dpn1_converted"
pause
