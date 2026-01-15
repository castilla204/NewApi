# Solución: Error "Host desconocido" con Direct Connection

## Problema

```
pg_dump: error: could not translate host name "db.rveqsehzlvbttlpmsbmi.supabase.co" to address: Host desconocido.
```

Este error indica que:
1. El project reference puede ser incorrecto
2. IPv6 no está habilitado o no funciona en tu sistema/red
3. El DNS no puede resolver el hostname

## Soluciones

### ✅ Solución 1: Verificar Project Reference Correcto

**Paso 1: Obtener el hostname correcto desde Supabase Dashboard**

1. Ve a [Supabase Dashboard](https://app.supabase.com)
2. Selecciona tu proyecto
3. Ve a **Settings** → **Database**
4. Busca la sección **Connection string**
5. Haz clic en **"Direct connection"** (no Session Pooler)
6. Copia el hostname completo (será algo como `db.XXXXXXXXXX.supabase.co`)

**Paso 2: Verificar el hostname**

El formato debe ser exactamente:
```
db.[TU-PROJECT-REF].supabase.co
```

**Paso 3: Probar con el hostname correcto**

```bash
# Reemplaza [TU-PROJECT-REF] con el que obtuviste del dashboard
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.[TU-PROJECT-REF].supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

---

### ✅ Solución 2: Verificar IPv6 (Si el hostname es correcto)

**Probar conectividad IPv6:**

```bash
ping6 db.rveqsehzlvbttlpmsbmi.supabase.co
```

Si no funciona, IPv6 no está disponible. Usa la Solución 3.

---

### ✅ Solución 3: Actualizar pg_dump a versión 17+ y usar Session Pooler (RECOMENDADO)

Si Direct Connection no funciona por IPv6, la mejor solución es actualizar `pg_dump`:

**Paso 1: Descargar PostgreSQL 17**

1. Ve a: https://www.postgresql.org/download/windows/
2. Descarga el instalador de PostgreSQL 17
3. Durante la instalación, selecciona **solo "Command Line Tools"** (no necesitas el servidor completo)

**Paso 2: Verificar instalación**

```bash
pg_dump --version
# Debe mostrar: pg_dump (PostgreSQL) 17.x
```

**Paso 3: Usar Session Pooler (no requiere IPv6)**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

**Ventajas:**
- ✅ No requiere IPv6
- ✅ Funciona con cualquier red
- ✅ Más compatible

---

### ✅ Solución 4: Usar Docker (Si tienes Docker instalado)

Docker maneja IPv6 automáticamente:

```bash
docker run --rm -e PGPASSWORD='hrpQTD57m7H.C+&' postgres:17 pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  > supabase_dump.dump
```

---

### ✅ Solución 5: Usar Herramientas Gráficas

**pgAdmin:**
1. Descarga desde: https://www.pgadmin.org/download/
2. Conecta usando la connection string del dashboard
3. Click derecho en la base de datos → "Backup"

**DBeaver:**
1. Descarga desde: https://dbeaver.io/download/
2. Conecta usando la connection string del dashboard
3. Click derecho → "Tools" → "Export Data"

---

## Comandos por Orden de Prioridad

### 1. Verificar Project Reference (Primero)

```bash
# Obtén el hostname correcto del dashboard y úsalo aquí
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h db.[TU-PROJECT-REF-REAL].supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  -f supabase_dump.dump
```

### 2. Actualizar pg_dump y usar Session Pooler (Si Direct Connection falla)

```bash
# Después de actualizar pg_dump a versión 17+
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

### 3. Usar Docker (Alternativa)

```bash
docker run --rm -e PGPASSWORD='hrpQTD57m7H.C+&' postgres:17 pg_dump -Fc -v --schema=public \
  -h db.rveqsehzlvbttlpmsbmi.supabase.co \
  -p 5432 \
  -U postgres \
  -d postgres \
  > supabase_dump.dump
```

---

## Troubleshooting

### Error: "Host desconocido"
**Causa**: Project reference incorrecto o IPv6 no disponible

**Solución**: 
1. Verifica el project reference en el dashboard
2. Si es correcto, actualiza `pg_dump` a versión 17+ y usa Session Pooler

### Error: "connection timeout" con Direct Connection
**Causa**: IPv6 no funciona en tu red

**Solución**: Actualiza `pg_dump` a versión 17+ y usa Session Pooler

### Error: "password authentication failed"
**Causa**: Credenciales incorrectas

**Solución**: Verifica la contraseña en el dashboard de Supabase

---

## Recomendación Final

**Para tu caso específico:**

1. **Primero**: Verifica el project reference en Supabase Dashboard → Settings → Database → "Direct connection"
2. **Si el hostname es correcto pero sigue fallando**: Actualiza `pg_dump` a versión 17+ y usa Session Pooler (no requiere IPv6)
3. **Alternativa rápida**: Usa Docker si lo tienes instalado

**Comando más confiable (después de actualizar pg_dump):**

```bash
PGPASSWORD='hrpQTD57m7H.C+&' pg_dump -Fc -v --schema=public \
  -h aws-1-eu-west-2.pooler.supabase.com \
  -p 5432 \
  -U postgres.rveqsehzlvbttlpmsbmi \
  -d postgres \
  -f supabase_dump.dump
```

Este comando usa Session Pooler que no requiere IPv6 y funciona con `pg_dump` 17+.
