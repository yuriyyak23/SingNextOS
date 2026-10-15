@echo off
setlocal EnableExtensions EnableDelayedExpansion

pushd "%~dp0" || exit /b 1

if exist ".vs\" rd /s /q ".vs"
if exist "TestResults\" rd /s /q "TestResults"
for /d /r %%D in (.phase8-build) do if exist "%%D\" rd /s /q "%%D"
for /d /r %%D in (obj) do if exist "%%D\" rd /s /q "%%D"
for /d /r %%D in (bin) do if exist "%%D\" rd /s /q "%%D"

for /r %%F in (*.csproj) do (
    set "ProjectDir=%%~dpF"
    if exist "!ProjectDir!TestResults\" rd /s /q "!ProjectDir!TestResults"
)

popd

echo Clean complete.
