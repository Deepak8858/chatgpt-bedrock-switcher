@echo off
setlocal
cd /d "%~dp0"
echo Running isolated configuration checks. Your real account files are not used.
ProviderSwitch.Checks.exe --report configuration-test-report.json --fixtures generated-fixtures
set "CONFIG_RESULT=%ERRORLEVEL%"
echo Running Windows UI smoke checks with temporary settings.
start "" /wait "%~dp0ProviderSwitch.exe" --ui-smoke-test "%CD%\windows-ui-test-report.json"
set "UI_RESULT=%ERRORLEVEL%"
echo Reports: configuration-test-report.json and windows-ui-test-report.json
if not "%CONFIG_RESULT%"=="0" exit /b 1
if not "%UI_RESULT%"=="0" exit /b 1
echo All executed Windows checks passed. Review the report for any skipped checks.
exit /b 0
