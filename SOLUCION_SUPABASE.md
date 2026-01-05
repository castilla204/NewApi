# Solución para `dotnet ef database update` con Supabase

## Problema Identificado

El error "Host desconocido" ocurre porque:
1. La conexión directa de Supabase (`db.rveqsehzlvbttlpmsbmi.supabase.co`) resuelve a IPv6
2. Npgsql en Windows no puede conectarse por IPv6 desde tu máquina
3. El pooler requiere la región correcta y el formato exacto del username

## Solución: Obtener la Cadena de Conexión Exacta

**PASOS:**

1. Ve a tu proyecto en Supabase Dashboard: https://supabase.com/dashboard/project/rveqsehzlvbttlpmsbmi
2. Navega a: **Settings** → **Database** → **Connection string**
3. Selecciona **Session mode** (puerto 5432)
4. Copia la cadena de conexión EXACTA que te proporciona
5. Convierte el formato URI a formato Npgsql si es necesario

## Formato Esperado

La cadena de conexión del Session Pooler debería tener este formato:
```
postgresql://postgres.rveqsehzlvbttlpmsbmi:[PASSWORD]@aws-0-[REGION].pooler.supabase.com:5432/postgres
```

O en formato Npgsql:
```
Host=aws-0-[REGION].pooler.supabase.com;Port=5432;Username=postgres.rveqsehzlvbttlpmsbmi;Password=[PASSWORD];Database=postgres;SslMode=Require;
```

**IMPORTANTE:** La región `[REGION]` debe ser la correcta para tu proyecto. Puede ser:
- `us-east-1`
- `eu-central-1`
- `eu-west-1`
- `eu-west-2`
- Otra región según donde esté alojado tu proyecto

## Alternativa: Aplicar Migraciones con MCP

Si `dotnet ef database update` sigue fallando, puedes aplicar las migraciones directamente usando el MCP de Supabase:

1. El script SQL ya está generado en `migrations.sql`
2. Puedes aplicarlo desde el SQL Editor de Supabase Dashboard
3. O usar el MCP de Supabase para aplicarlo automáticamente

## Estado Actual

- ✅ Script de migraciones generado: `migrations.sql` (68 migraciones)
- ✅ MCP de Supabase configurado y funcionando
- ❌ `dotnet ef database update` falla por problemas de conectividad IPv6/IPv4





