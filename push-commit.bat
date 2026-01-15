@echo off
git config core.pager ""
git add appsettings.json appsettings.Development.json
git commit -m "FIX: Restaurar puerto 5432 despues de rollback"
git push origin HEAD
pause



