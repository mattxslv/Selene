@echo off
rem Selene as a new user gets it, in a Playnite folder of its own (see public-test.ps1).
rem   public-test.cmd            brand-new Playnite + the packages in dist\, starts Fullscreen
rem   public-test.cmd keep       keep the library and Selene's settings, only reinstall the packages
rem   public-test.cmd release    install the real public release from GitHub instead of dist\
rem   public-test.cmd desktop    start Desktop mode (to add games)
rem   public-test.cmd wizard     no config: Playnite's own first-run wizard shows first
rem Words can be combined: public-test.cmd keep release
set HERE=%~dp0
set ARGS=
:next
if "%~1"=="" goto run
if /i "%~1"=="keep" set ARGS=%ARGS% -Keep
if /i "%~1"=="release" set ARGS=%ARGS% -Release
if /i "%~1"=="desktop" set ARGS=%ARGS% -Desktop
if /i "%~1"=="wizard" set ARGS=%ARGS% -Wizard
if /i "%~1"=="prepare" set ARGS=%ARGS% -PrepareOnly
shift
goto next
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%public-test.ps1" %ARGS%
if errorlevel 1 pause
