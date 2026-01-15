# Solución: Error de Versión de pg_dump

## Problema

```
pg_dump: error: aborting because of server version mismatch
pg_dump: detail: server version: 17.6; pg_dump version: 16.2
```

El servidor de Supabase usa PostgreSQL 17.6, pero tu `pg_dump` es versión 16.2. `pg_dump` debe ser igual o más reciente que la versión del servidor.

## Soluciones

### ✅ Solución 1: Usar Direct Connection (Recomendada)

El Session Pooler (`pooler.supabase.com`) no es compatible con `pg_dump`. Usa la Direct Connection que soporta todas las características de PostgreSQL 17.

**Formato de Direct Connection:**
```
postgresql://postgres:[YOUR-PASSWORD]@db.[PROJECT-REF].supabase.co:5432/postgres
```

**Cómo obtener tu Project Reference:**
1. Ve a tu proyecto en [Supabase Dashboard](https://app.supabase.com)
2. Ve a **Settings** → **Database**
3. Busca "Connection string" → "Direct connection"
4. El hostname será: `db.[TU-PROJECT-REF].supabase.co`

**Comando actualizado (Git Bash/MINGW64):**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

**Diferencias importantes:**
- Hostname: `db.[PROJECT-REF].supabase.co` (Direct Connection) en lugar de `pooler.supabase.com`
- Username: `postgres` (sin el prefijo `postgres.`)
- Requiere IPv6 habilitado en tu sistema
- **IMPORTANTE**: Reemplaza `rveqsehzlvbttlpmsbmi` con tu project reference real

**Verificar IPv6:**
```bash
ping6 db.rveqsehzlvbttlpmsbmi.supabase.co
```

Si no tienes IPv6, usa la Solución 2 o 3.

---

### ✅ Solución 2: Actualizar pg_dump a versión 17+

1. **Descargar PostgreSQL 17:**
   - Windows: https://www.postgresql.org/download/windows/
   - Selecciona "Command Line Tools" durante la instalación

2. **Verificar versión:**
```bash
pg_dump --version
# Debe mostrar: pg_dump (PostgreSQL) 17.x
```

3. **Usar el comando original:**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

---

### ✅ Solución 3: Usar Docker (Si tienes Docker instalado)

Docker te permite usar cualquier versión de PostgreSQL sin instalar nada:

```bash
docker run --rm -e PGPASSWORD='hrpQTD57m7H.C+&' postgres:17 pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  > supabase_dump.dump
```

**Ventajas:**
- No necesitas instalar PostgreSQL
- Siempre usa la versión más reciente
- Funciona en cualquier sistema

---

### ✅ Solución 4: Usar pgAdmin o DBeaver

Herramientas gráficas que manejan automáticamente las versiones:

1. **pgAdmin:**
   - Descarga desde: https://www.pgadmin.org/download/
   - Conecta a Supabase
   - Usa "Backup" para exportar

2. **DBeaver:**
   - Descarga desde: https://dbeaver.io/download/
   - Conecta a Supabase
   - Click derecho en la base de datos → "Tools" → "Export Data"

---

## Comandos Completos Actualizados

### Exportar usando Direct Connection (Recomendado)

**Formato de Direct Connection:**
```
postgresql://postgres:[YOUR-PASSWORD]@db.[PROJECT-REF].supabase.co:5432/postgres
```

**Obtener tu Project Reference:**
- Ve a Supabase Dashboard → Settings → Database → "Direct connection"
- El hostname será: `db.[TU-PROJECT-REF].supabase.co`

**Formato Custom:**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

**Formato SQL:**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  --clean --if-exists \
  -f backup_supabase.sql
```

**Nota**: Reemplaza `rveqsehzlvbttlpmsbmi` con tu project reference real obtenido del dashboard.

### Importar a Render (Sin cambios)

```bash
PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF' pg_restore -v -d inspecciono --no-owner --no-acl --clean \
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com \
  -p 5432 \
  -U inspecciono_user \
  supabase_dump.dump
```

---

## Troubleshooting

### Error: "connection to server failed" con Direct Connection

**Causa**: IPv6 no está habilitado o no funciona en tu red.

**Solución**: 
- Usa Solución 2 (actualizar pg_dump) y usa Session Pooler
- O usa Solución 3 (Docker)

### Error: "role does not exist"

**Causa**: Usaste el username incorrecto.

**Solución**: 
- Direct Connection usa: `-U postgres`
- Session Pooler usa: `-U postgres.rveqsehzlvbttlpmsbmi`

### Error: "server closed the connection unexpectedly"

**Causa**: Session Pooler no soporta `pg_dump`.

**Solución**: Usa Direct Connection (`db.*.supabase.co`) en lugar de Session Pooler (`pooler.supabase.com`)

---

## Resumen

| Método | Hostname | Username | Requiere IPv6 | Compatible con pg_dump 16 |
|--------|----------|----------|---------------|---------------------------|
| Direct Connection | `db.*.supabase.co` | `postgres` | ✅ Sí | ✅ Sí (pero mejor 17+) |
| Session Pooler | `pooler.supabase.com` | `postgres.PROJECT_REF` | ❌ No | ❌ No (necesita 17+) |
| Docker | `db.*.supabase.co` | `postgres` | ✅ Sí | ✅ Sí (usa versión 17) |

**Recomendación**: Usa Direct Connection con el comando actualizado. Si no funciona por IPv6, actualiza `pg_dump` a versión 17+.
