# Comando para Importar a Render PostgreSQL

## Comando Completo (PowerShell)

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
pg_restore -v -d inspecciono --no-owner --no-acl --clean -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user supabase_dump.dump
```

## Comando Completo (Linux/Mac/Git Bash)

```bash
PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF' pg_restore -v -d inspecciono --no-owner --no-acl --clean -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user supabase_dump.dump
```

## Explicación de Opciones

- `-v`: Modo verbose (muestra progreso detallado)
- `-d inspecciono`: Base de datos destino
- `--no-owner`: No intenta establecer ownership (evita errores cuando los usuarios de Supabase no existen en Render)
- `--no-acl`: No restaura permisos ACL (evita errores de permisos entre diferentes servidores)
- `--clean`: Elimina objetos existentes antes de crearlos (limpia la base de datos)
- `-h`: Hostname de Render PostgreSQL
- `-p 5432`: Puerto
- `-U inspecciono_user`: Usuario
- `supabase_dump.dump`: Archivo dump a importar

## Credenciales Render

- **Hostname externo**: `dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com`
- **Hostname interno**: `dpg-d5kar5l6ubrc73espd5g-a` (solo para servicios dentro de Render)
- **Puerto**: `5432`
- **Database**: `inspecciono`
- **Username**: `inspecciono_user`
- **Password**: `nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF`

## Notas Importantes

⚠️ **ADVERTENCIA**: 
- El comando `--clean` eliminará todos los objetos existentes en la base de datos antes de importar
- Asegúrate de tener un backup antes de ejecutar
- Las opciones `--no-owner` y `--no-acl` son **críticas** para evitar errores de permisos

✅ **Por qué usar `--no-owner` y `--no-acl`:**
- Los usuarios y roles de Supabase no existen en Render
- Evita errores como "role does not exist"
- Los objetos se crearán con el usuario actual (`inspecciono_user`)
- Los permisos se establecerán según las políticas por defecto de Render

## Verificar Importación

Después de importar, verifica que todo se haya migrado correctamente:

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
psql -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user -d inspecciono -c "\dt"
```

Esto mostrará todas las tablas importadas.
