# 🔍 Aclaración: Secretos en Desarrollo

## ✅ Lo que YA funcionaba

El código **YA intentaba obtener secretos de Google Cloud Secret Manager en desarrollo**:

1. **Inicialización del cliente**: El código ya inicializa `SecretManagerServiceClient` en desarrollo
   - Busca credenciales en `GOOGLE_APPLICATION_CREDENTIALS`
   - Si no encuentra, usa fallback a `C:\cloudcredential.json` (solo en desarrollo)

2. **Obtención de secretos**: La función `GetSecretValue` ya intentaba obtener secretos de GCSM
   - Funcionaba igual en desarrollo y producción
   - Usaba los **MISMOS nombres de secretos** en ambos entornos

## 🔄 Lo que cambió con la modificación

**ANTES:**
- Desarrollo: Intentaba obtener `jwt-key` (el mismo que producción)
- Producción: Intentaba obtener `jwt-key`

**AHORA:**
- Desarrollo: Intenta primero `jwt-key-dev`, luego `jwt-key` como fallback
- Producción: Intenta obtener `jwt-key` (sin cambios)

## ❌ Por qué fallaba antes

El error "JWT Key not found" ocurría porque:

1. **No tenía credenciales de Google Cloud configuradas** en desarrollo local
   - No existía `C:\cloudcredential.json`
   - O no estaba configurada la variable `GOOGLE_APPLICATION_CREDENTIALS`

2. **No podía conectarse a GCSM** desde su máquina local
   - Problemas de red/firewall
   - Credenciales inválidas

3. **No tenía variables de entorno** configuradas como fallback
   - No había archivo `.env`
   - No había variables de entorno del sistema

## ✅ Soluciones (en orden de prioridad)

### Opción 1: Configurar Google Cloud en Desarrollo (Recomendado)

Si quieres usar GCSM en desarrollo (como en producción):

1. **Obtener credenciales de Google Cloud**:
   ```bash
   # Desde tu máquina local
   gcloud auth application-default login
   # O descargar el archivo JSON desde GCP Console
   ```

2. **Colocar credenciales**:
   - Opción A: Colocar en `C:\cloudcredential.json` (el código lo busca automáticamente)
   - Opción B: Configurar variable de entorno:
     ```powershell
     $env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\tu\cloudcredential.json"
     ```

3. **Crear secretos de desarrollo** (con sufijo `-dev`):
   ```bash
   gcloud secrets create jwt-key-dev --project=grup-441318
   echo "tu_jwt_key_desarrollo" | gcloud secrets versions add jwt-key-dev \
     --data-file=- --project=grup-441318
   ```

4. **Probar**: Ejecutar la aplicación y verificar que obtiene secretos de GCSM

### Opción 2: Usar Variables de Entorno (Más Simple)

Si prefieres no usar GCSM en desarrollo:

1. **Crear archivo `.env`** con los secretos:
   ```env
   JWT_KEY=tu_jwt_key_aqui
   JWT_ISSUER=newApi
   JWT_AUDIENCE=newApi
   ```

2. **Instalar DotNetEnv**:
   ```bash
   dotnet add package DotNetEnv
   ```

3. **Cargar `.env` en Program.cs** (agregar al inicio):
   ```csharp
   using DotNetEnv;
   
   var builder = WebApplication.CreateBuilder(args);
   
   if (builder.Environment.IsDevelopment())
   {
       var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
       if (File.Exists(envPath))
       {
           Env.Load(envPath);
       }
   }
   ```

### Opción 3: Usar User Secrets de .NET

```bash
dotnet user-secrets init
dotnet user-secrets set "Jwt:Key" "tu_jwt_key_aqui"
```

## 🎯 Recomendación

**Para desarrollo local**, la opción más práctica es:

1. **Usar variables de entorno** (archivo `.env`) para desarrollo rápido
2. **Usar GCSM** cuando necesites probar con secretos reales o similares a producción

La modificación que hice permite ambas opciones:
- Si tienes credenciales de GCSM → usa secretos `-dev` de GCSM
- Si no tienes credenciales → usa variables de entorno (`.env`)

## 📊 Flujo Actual (Después de la Modificación)

```
DESARROLLO:
1. Intenta obtener de GCSM: jwt-key-dev
2. Si no existe, intenta: jwt-key (fallback)
3. Si GCSM no disponible, usa variables de entorno
4. Si no hay variables, usa configuración (appsettings.json)

PRODUCCIÓN:
1. Intenta obtener de GCSM: jwt-key
2. Si GCSM no disponible, usa variables de entorno
3. Si no hay variables, usa configuración
```

## ✅ Resumen

- **SÍ**, el código ya intentaba usar GCSM en desarrollo
- **PERO** usaba los mismos secretos que producción
- **AHORA** puede usar secretos diferentes (`-dev`) si los creas
- **SIGUE** funcionando igual si no creas secretos `-dev` (usa los normales)

La modificación es **compatible hacia atrás**: si no creas secretos `-dev`, seguirá usando los secretos normales.

