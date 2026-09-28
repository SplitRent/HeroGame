@echo off
rem Builds the realistic human body (SK_Human.fbx) from MakeHuman's CC0 data in your MPFB add-on.
rem Usage: double-click, or   build_human.bat "C:\path\to\YourUnityProject\Assets\_Project\Art"
setlocal EnableDelayedExpansion
set "BLENDER="
for /d %%D in ("%ProgramFiles%\Blender Foundation\Blender*") do if exist "%%D\blender.exe" set "BLENDER=%%D\blender.exe"
if not defined BLENDER (
  echo Could not find Blender under "%ProgramFiles%\Blender Foundation". Install Blender 4.2 or newer.
  pause
  exit /b 1
)
set "MPFB="
for /d %%V in ("%APPDATA%\Blender Foundation\Blender\*") do (
  for /d %%R in ("%%V\extensions\*") do if exist "%%R\mpfb\data\3dobjs\base.obj" set "MPFB=%%R\mpfb"
)
if not defined MPFB (
  echo Could not find the MPFB extension. In Blender: Edit ^> Preferences ^> Get Extensions, search MPFB, Install.
  pause
  exit /b 1
)
set "ART=%~1"
if "%ART%"=="" set "ART=%~dp0..\..\Game\Assets\_Project\Art"
echo Blender: %BLENDER%
echo MPFB:    %MPFB%
echo Output:  %ART%\Characters\Human
"%BLENDER%" -b -P "%~dp0cli.py" -- human --mpfb "%MPFB%" --art-root "%ART%"
echo.
echo Done. Copy the report above into the chat if anything says "missing".
pause
