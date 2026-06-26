@echo off
:: WiseSVN UPM Packaging — delegates to Scripts/pack.sh
:: Usage: makeupm.bat <version> [--dry-run] [--no-push]
:: Example: makeupm.bat 1.6.0 --dry-run

setlocal

:: Locate Git Bash. Prefer PATH, then standard install locations.
where bash >nul 2>nul
if %ERRORLEVEL%==0 (
    bash Scripts/pack.sh %*
    goto :end
)

if exist "%ProgramFiles%\Git\bin\bash.exe" (
    "%ProgramFiles%\Git\bin\bash.exe" Scripts/pack.sh %*
    goto :end
)

if exist "%ProgramFiles(x86)%\Git\bin\bash.exe" (
    "%ProgramFiles(x86)%\Git\bin\bash.exe" Scripts/pack.sh %*
    goto :end
)

if exist "%LocalAppData%\Programs\Git\bin\bash.exe" (
    "%LocalAppData%\Programs\Git\bin\bash.exe" Scripts/pack.sh %*
    goto :end
)

echo ERROR: bash not found. Install Git for Windows or add bash to PATH.
echo See: https://git-scm.com/download/win
exit /b 1

:end
endlocal
pause
