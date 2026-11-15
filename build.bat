@echo off
rem ============================================================
rem  Build the local-mode tool with the C# compiler shipped with
rem  Windows. No SDK, no NuGet, no network. Keep this pure ASCII.
rem ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [X] csc.exe not found - needs .NET Framework 4.x
  exit /b 1
)

set "OUT=%~dp0L4D2LocalSetup.exe"
set "ICONARG="
if exist "%~dp0app.ico" set "ICONARG=/win32icon:"%~dp0app.ico""
set "SRCS="
set /a N=0
for %%F in ("%~dp0*.cs") do (
  set "SRCS=!SRCS! "%%~fF""
  set /a N+=1
)
if %N%==0 (
  echo [X] No .cs files next to build.bat
  exit /b 1
)
echo [i] compiling %N% source files

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:"%OUT%" %ICONARG% ^
  /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll ^
  /resource:"%~dp0desk\index.html",desk.html ^
  /resource:"%~dp0desk\three.min.js",three.js ^
  %SRCS%
if errorlevel 1 (
  echo.
  echo [X] Build failed.
  exit /b 1
)
echo [OK] Built: %OUT%
