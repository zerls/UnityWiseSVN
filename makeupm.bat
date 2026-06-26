@echo off
:: WiseSVN UPM Packaging — delegates to Scripts/pack.sh
:: Usage: makeupm.bat <version> [--dry-run] [--no-push]
:: Example: makeupm.bat 1.6.0 --dry-run
bash Scripts/pack.sh %*
pause
