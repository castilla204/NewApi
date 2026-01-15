# Migración a Render PostgreSQL

## Resumen de Cambios

La base de datos principal de la aplicación se ha migrado de **Supabase PostgreSQL** a **Render PostgreSQL**. Supabase se mantiene **SOLO** para funcionalidades de Realtime (chat en directo y notificaciones push).

## Motivación

- **PostgreSQL nativo**: Render ofrece PostgreSQL estándar sin poolers intermedios (Session/Transaction Pooler)
- **Mejor rendimiento**: Sin restricciones de prepared statements o multiplexing
- **Configuración simplificada**: Misma connection string para toda la aplicación
- **Compatibilidad total**: Soporta savepoints, ExecutionStrategy, Hangfire y locks distribuidos sin problemas
- **Costos optimizados**: Separar base de datos transaccional de servicios en tiempo real

## Arquitectura Actualizada

```
┌─────────────────────────────────────┐
│     RENDER POSTGRESQL               │
│  (Base de datos principal)          │
│                                     │
│  - Usuarios                         │
│  - Transacciones                    │
│  - Appointments                     │
│  - Todos los datos de negocio       │
│  - Hangfire jobs                    │
└─────────────────────────────────────┘

┌─────────────────────────────────────┐
│     SUPABASE REALTIME               │
│  (Solo chat y notificaciones)       │
│                                     │
│  - Chat en tiempo real              │
│  - Typing indicators                │
│  - Presencia de usuarios            │
│  - Notificaciones push              │
└─────────────────────────────────────┘
```

## Credenciales de Render PostgreSQL

### Desarrollo Local y Producción

**Hostname Externo** (para desarrollo local):
```
dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com
```

**Hostname Interno** (para servicios en Render):
```
dpg-d5kar5l6ubrc73espd5g-a
```

**Datos de conexión**:
- **Database**: inspecciono
- **Username**: inspecciono_user
- **Password**: nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF
- **Port**: 5432

**Connection String Externa** (desarrollo local):
```
Host=dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;
```

**Connection String Interna** (servicios en Render):
```
Host=dpg-d5kar5l6ubrc73espd5g-a;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;
```

## Archivos Modificados

### 1. `appsettings.json`

```json
{
  "ConnectionStrings": {
    "PostgresConnection": "Host=dpg-d5kar5l6ubrc73espd5g-a;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;"
  },
  "Supabase": {
    "Url": "TU_SUPABASE_URL_AQUI",
    "AnonKey": "TU_SUPABASE_ANON_KEY_AQUI",
    "ServiceRoleKey": "TU_SUPABASE_SERVICE_ROLE_KEY_AQUI"
  }
}
```

### 2. `appsettings.Development.json`

```json
{
  "ConnectionStrings": {
    "PostgresConnection": "Host=dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;"
  },
  "Supabase": {
    "Url": "TU_SUPABASE_URL_AQUI",
    "AnonKey": "TU_SUPABASE_ANON_KEY_AQUI",
    "ServiceRoleKey": "TU_SUPABASE_SERVICE_ROLE_KEY_AQUI"
  }
}
```

### 3. `Program.cs`

**Cambios principales**:

1. **Connection string hardcodeada en producción** actualizada a Render (línea ~740):
```csharp
connectionString = "Host=dpg-d5kar5l6ubrc73espd5g-a;Port=5432;Database=inspecciono;Username=inspecciono_user;Password=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;";
```

2. **Configuración de Hangfire simplificada**: Ya no necesita detectar tipos de poolers de Supabase

3. **DbContext simplificado**: Configuración estándar de PostgreSQL sin workarounds especiales

## Configuración de Supabase Realtime

### Obtener Credenciales de Supabase

1. Ve a tu proyecto en [Supabase Dashboard](https://app.supabase.com)
2. Ve a **Settings** → **API**
3. Copia las siguientes credenciales:
   - **Project URL**: `https://[project-ref].supabase.co`
   - **anon public key**: Tu clave anónima (empieza con `eyJ...`)
   - **service_role key**: Tu clave de servicio (empieza con `eyJ...`)

### Actualizar Configuración

**Para desarrollo local**, edita `appsettings.Development.json`:

```json
{
  "Supabase": {
    "Url": "https://[tu-project-ref].supabase.co",
    "AnonKey": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "ServiceRoleKey": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
  }
}
```

**Para producción (Render.com)**, configura las siguientes variables de entorno:

```bash
SUPABASE_URL=https://[tu-project-ref].supabase.co
SUPABASE_SERVICE_KEY=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

## Migración de Base de Datos

### ⚠️ IMPORTANTE: Problema de Versión de pg_dump

Si recibes el error: `server version: 17.6; pg_dump version: 16.2`

**Solución 1 (Recomendada): Usar Direct Connection de Supabase**

El Session Pooler no es compatible con `pg_dump`. Usa la Direct Connection que soporta PostgreSQL 17.

**Formato de Direct Connection:**
```
postgresql://postgres:[YOUR-PASSWORD]@db.[PROJECT-REF].supabase.co:5432/postgres
```

**Cómo obtener tu Project Reference:**
1. Ve a tu proyecto en [Supabase Dashboard](https://app.supabase.com)
2. Ve a **Settings** → **Database**
3. Busca "Connection string" → "Direct connection"
4. El hostname será: `db.[TU-PROJECT-REF].supabase.co`

**Comando con Direct Connection (Git Bash/MINGW64):**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

**Windows PowerShell:**
```powershell
$env:PGPASSWORD='hrpQTD57m7H.C+&'
pg_dump -Fc -v --schema=public `
  -h db.rveqsehzlvbttlpmsbmi.supabase.co `
  -p 5432 `
  -U postgres `
  -d postgres `
  -f supabase_dump.dump
```

**Nota**: 
- Direct Connection requiere IPv6 habilitado
- El project reference (`rveqsehzlvbttlpmsbmi`) debe coincidir con tu proyecto
- Si no funciona, usa la Solución 2 o verifica tu project reference en el dashboard

**Solución 2: Actualizar pg_dump a versión 17+**

1. Descarga PostgreSQL 17 desde: https://www.postgresql.org/download/windows/
2. Instala solo las "Command Line Tools"
3. Asegúrate de que la nueva versión esté en tu PATH

**Solución 3: Usar Docker (si tienes Docker instalado)**

```bash
docker run --rm -e PGPASSWORD='hrpQTD57m7H.C+&' postgres:17 pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  > supabase_dump.dump
```

### Paso 1: Exportar datos de Supabase

**Opción A: Formato Custom usando Direct Connection (Recomendado)**

**Formato de Direct Connection:**
```
postgresql://postgres:[YOUR-PASSWORD]@db.[PROJECT-REF].supabase.co:5432/postgres
```

**Obtener tu Project Reference:**
- Ve a Supabase Dashboard → Settings → Database → "Direct connection"
- El hostname será: `db.[TU-PROJECT-REF].supabase.co`

**Linux/Mac/Git Bash:**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

**Windows PowerShell:**
```powershell
$env:PGPASSWORD='hrpQTD57m7H.C+&'
pg_dump -Fc -v --schema=public `
  -h db.rveqsehzlvbttlpmsbmi.supabase.co `
  -p 5432 `
  -U postgres `
  -d postgres `
  -f supabase_dump.dump
```

**Nota**: 
- Reemplaza `rveqsehzlvbttlpmsbmi` con tu project reference real
- Direct Connection requiere IPv6 habilitado
- Si no funciona, intenta actualizar `pg_dump` a versión 17+ o usa Docker

**Opción B: Formato SQL usando Direct Connection**

**Linux/Mac/Git Bash:**
```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  --clean --if-exists \
  -f backup_supabase.sql
```

**Windows PowerShell:**
```powershell
$env:PGPASSWORD='hrpQTD57m7H.C+&'
pg_dump -v --schema=public `
  -h db.rveqsehzlvbttlpmsbmi.supabase.co `
  -p 5432 `
  -U postgres `
  -d postgres `
  --clean --if-exists `
  -f backup_supabase.sql
```

### Paso 2: Importar a Render PostgreSQL

**Si usaste formato Custom (-Fc):**

**Linux/Mac/Git Bash:**
```bash
PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF' pg_restore -v -d inspecciono --no-owner --no-acl --clean \
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com \
  -p 5432 \
  -U inspecciono_user \
  supabase_dump.dump
```

**Windows PowerShell:**
```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
pg_restore -v -d inspecciono --no-owner --no-acl --clean `
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com `
  -p 5432 `
  -U inspecciono_user `
  supabase_dump.dump
```

**Explicación de opciones importantes:**
- `-v`: Modo verbose (muestra progreso detallado)
- `-d inspecciono`: Especifica la base de datos destino
- `--no-owner`: No intenta establecer ownership de objetos (evita errores cuando los usuarios no existen en Render)
- `--no-acl`: No restaura permisos ACL (evita errores de permisos entre diferentes servidores)
- `--clean`: Elimina objetos existentes antes de crearlos (limpia la base de datos)

**Si usaste formato SQL:**

**Linux/Mac/Git Bash:**
```bash
PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF' psql \
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com \
  -p 5432 \
  -U inspecciono_user \
  -d inspecciono \
  -f backup_supabase.sql
```

**Windows PowerShell:**
```powershell
$env:PGPASSWORD='nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF'
psql `
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com `
  -p 5432 `
  -U inspecciono_user `
  -d inspecciono `
  -f backup_supabase.sql
```

**Nota**: El formato Custom (`-Fc`) es más eficiente para bases de datos grandes ya que:
- Comprime los datos automáticamente
- Permite restaurar objetos específicos
- Es más rápido en la importación/exportación
- Requiere usar `pg_restore` en lugar de `psql`

### Paso 3: Ejecutar migraciones de Entity Framework

Si hay migraciones pendientes:

```bash
dotnet ef database update
```

## Ventajas de la Nueva Arquitectura

### Render PostgreSQL

✅ **PostgreSQL nativo**: Sin restricciones de poolers intermedios  
✅ **Mejor rendimiento**: Transacciones y locks más eficientes  
✅ **Configuración simple**: Una sola connection string  
✅ **Sin workarounds**: No necesita configuraciones especiales  
✅ **Escalabilidad**: Fácil upgrade de plan cuando sea necesario  

### Supabase Realtime

✅ **Especializado en tiempo real**: Optimizado para chat y notificaciones  
✅ **Escalabilidad global**: CDN y servidores distribuidos  
✅ **Presence integrado**: Sin diccionarios en memoria  
✅ **WebSockets optimizados**: Mejor que SignalR para casos de uso simples  

## Problemas Resueltos

### 1. ObjectDisposedException en Hangfire
- **Antes**: Session Pooler de Supabase cerraba conexiones prematuramente
- **Ahora**: Render PostgreSQL mantiene conexiones estables

### 2. Prepared Statements
- **Antes**: Transaction Pooler no soportaba prepared statements
- **Ahora**: Soporte completo en Render PostgreSQL

### 3. Savepoints y ExecutionStrategy
- **Antes**: Requería configuración especial según tipo de pooler
- **Ahora**: Funciona sin configuración adicional

### 4. Multiplexing
- **Antes**: Había que deshabilitarlo para Transaction Pooler
- **Ahora**: Se puede usar según necesidades reales

## Testing

### Verificar Conexión a Render PostgreSQL

```bash
PGPASSWORD=nRtnnNtagS7jPmaYVxz18BF0wtwkj4gF psql \
  -h dpg-d5kar5l6ubrc73espd5g-a.frankfurt-postgres.render.com \
  -U inspecciono_user \
  -d inspecciono \
  -c "SELECT version();"
```

### Verificar Hangfire

1. Ejecuta la aplicación
2. Ve a `/hangfire` en tu navegador
3. Verifica que los jobs se ejecuten sin errores

### Verificar Supabase Realtime

1. Asegúrate de tener las credenciales configuradas
2. Verifica logs al iniciar la app: "Supabase Realtime configured"
3. Prueba enviando un mensaje en el chat

## Rollback (Si es necesario)

Si necesitas volver a Supabase como base de datos principal:

1. **Restaurar `appsettings.json`**:
```json
{
  "ConnectionStrings": {
    "PostgresConnection": "User Id=postgres.rveqsehzlvbttlpmsbmi;Password=hrpQTD57m7H.C+&;Server=aws-1-eu-west-2.pooler.supabase.com;Port=5432;Database=postgres;SslMode=Require;Timeout=60;CommandTimeout=120;Pooling=true;Multiplexing=false;Enlist=false;Max Auto Prepare=0;KeepAlive=30;"
  }
}
```

2. **Restaurar hardcoded connection en `Program.cs`** (línea ~740)

3. **Restaurar lógica de detección de poolers** en `Program.cs`

## Próximos Pasos

1. ✅ Configurar credenciales de Supabase para Realtime
2. ⏳ Ejecutar migración de datos de Supabase a Render
3. ⏳ Verificar que todos los servicios funcionen correctamente
4. ⏳ Monitorear rendimiento durante 48 horas
5. ⏳ Eliminar datos de Supabase PostgreSQL (mantener solo Realtime)

## Soporte

- **Render PostgreSQL**: https://render.com/docs/databases
- **Supabase Realtime**: https://supabase.com/docs/guides/realtime
- **Hangfire**: https://docs.hangfire.io/en/latest/

## Notas de Seguridad

⚠️ **IMPORTANTE**: Las credenciales en este documento son de ejemplo. En producción:

1. Usa **Google Cloud Secret Manager** o variables de entorno de Render
2. No commits credenciales en git
3. Rota las contraseñas regularmente
4. Usa conexiones SSL (ya configurado)

---

**Fecha de migración**: 15 de enero de 2026  
**Autor**: Cursor AI Assistant  
**Estado**: ✅ Migración completada, pendiente migración de datos
