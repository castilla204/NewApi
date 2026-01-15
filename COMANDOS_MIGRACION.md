# Comandos de Migración - Listos para Ejecutar

## Opción 1: Usar el Script Automatizado (Recomendado)

```powershell
.\Scripts\MigrateToRender.ps1
```

El script te guiará paso a paso y manejará todos los detalles.

---

## Opción 2: Comandos Manuales

### Paso 1: Exportar de Supabase (Formato Custom - Recomendado)

⚠️ **IMPORTANTE**: Si recibes error de versión, usa Direct Connection en lugar de Session Pooler:

**Opción A: Direct Connection (Recomendado - funciona con pg_dump 16+)**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

**Opción B: Session Pooler (Requiere pg_dump 17+)**
```powershell
$env:PGPASSWORD='hrpQTD57m7H.C+&'
pg_dump -Fc -v --schema=public -h aws-1-eu-west-2.pooler.supabase.com -p 5432 -U postgres.rveqsehzlvbttlpmsbmi -d postgres -f supabase_dump.dump
```

**Nota**: Direct Connection requiere IPv6. Si no funciona, actualiza `pg_dump` a versión 17+ o usa Docker.

### Paso 2: Importar a Render (Formato Custom)

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
pg_restore -v -d inspecciono --no-owner --no-acl --clean -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user supabase_dump.dump
```

**Explicación de opciones:**
- `-v`: Modo verbose (muestra progreso)
- `-d inspecciono`: Base de datos destino
- `--no-owner`: No intenta establecer ownership (evita errores de permisos)
- `--no-acl`: No restaura permisos ACL (evita errores de permisos)
- `--clean`: Elimina objetos antes de crearlos (limpia la base de datos)

---

## Opción 3: Formato SQL (Alternativa)

### Paso 1: Exportar de Supabase (Formato SQL)

**Usando Direct Connection (Recomendado):**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  --clean --if-exists \
  -f backup_supabase.sql
```

### Paso 2: Importar a Render (Formato SQL)

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
psql -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user -d inspecciono -f backup_supabase.sql
```

---

## Verificar Migración

### Verificar conexión a Render

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
psql -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user -d inspecciono -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public';"
```

### Verificar tablas migradas

```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
psql -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com -p 5432 -U inspecciono_user -d inspecciono -c "\dt"
```

---

## Después de la Migración

1. **Ejecutar migraciones de Entity Framework:**
```powershell
dotnet ef database update
```

2. **Verificar que la aplicación funcione:**
```powershell
dotnet run
```

3. **Verificar Hangfire:**
   - Abre `http://localhost:5000/hangfire` en tu navegador
   - Verifica que no haya errores

---

## Notas Importantes

⚠️ **ADVERTENCIA**: 
- La migración sobrescribirá los datos existentes en Render PostgreSQL
- Asegúrate de tener un backup antes de proceder
- El formato Custom (`-Fc`) es más rápido y eficiente para bases de datos grandes

✅ **Ventajas del formato Custom:**
- Comprime automáticamente (archivos más pequeños)
- Más rápido en importación/exportación
- Permite restaurar objetos específicos
- Requiere `pg_restore` en lugar de `psql`

✅ **Ventajas del formato SQL:**
- Texto plano, fácil de leer y editar
- Compatible con cualquier herramienta SQL
- Puede ser modificado antes de importar
- Usa `psql` estándar

---

## Troubleshooting

### Error: "server version: 17.6; pg_dump version: 16.2"
**Solución**: 
- Usa Direct Connection (`db.rveqsehzlvbttlpmsbmi.supabase.co`) en lugar de Session Pooler
- O actualiza `pg_dump` a versión 17+ desde https://www.postgresql.org/download/windows/
- O usa Docker: `docker run --rm postgres:17 pg_dump ...`

Ver guía completa en `SOLUCION_ERROR_VERSION_PGDUMP.md`

### Error: "pg_dump: command not found"
**Solución**: Instala PostgreSQL Client Tools desde https://www.postgresql.org/download/windows/

### Error: "connection to server failed" (Direct Connection)
**Solución**: Direct Connection requiere IPv6. Verifica con `ping6 db.rveqsehzlvbttlpmsbmi.supabase.co`. Si no funciona, actualiza `pg_dump` a versión 17+ y usa Session Pooler.

### Error: "password authentication failed"
**Solución**: Verifica las credenciales en los comandos

### Error: "connection timeout"
**Solución**: Verifica que el hostname y puerto sean correctos, y que tu IP esté permitida en Render

### Error: "database does not exist"
**Solución**: Asegúrate de que la base de datos `inspecciono` exista en Render
