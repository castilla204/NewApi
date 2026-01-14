# Configurar git para no usar paginador
$env:GIT_PAGER = 'cat'
git config --global core.pager cat

# Hacer rollback al commit especificado
git reset --hard 9d286004a7571eb3eee6c482bdeef145d384732e

# Verificar el estado
git log --oneline -1

