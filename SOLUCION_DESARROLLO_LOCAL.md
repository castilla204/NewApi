# 🔧 Solución: Error "JWT Key not found" en Desarrollo Local

## ❌ Problema

Al ejecutar la aplicación en desarrollo local (Windows), obtienes el error:
```
System.InvalidOperationException: 'JWT Key not found'
```

## 🔍 Causa

La aplicación intenta obtener `JWT_KEY` de estas fuentes (en orden de prioridad):
1. ✅ Variable de entorno `JWT_KEY`
2. ⚠️ Google Cloud Secret Manager (requiere credenciales configuradas)
3. ⚠️ `appsettings.json` (está comentada)

En desarrollo local, ninguna de estas fuentes tiene el valor.

## ✅ Soluciones

### Opción 1: Crear archivo `.env` y cargarlo (Recomendado)

#### Paso 1: Crear archivo `.env` en la raíz del proyecto

Crea un archivo `.env` en `/root/newapi/` (o en tu máquina local: `C:\Users\Diego\Downloads\App\App\NewApi\`) con:

```env
# JWT Configuration
JWT_KEY=ThisIsA32CharacterLongSecretKey123456
JWT_ISSUER=newApi
JWT_AUDIENCE=newApi

# PostgreSQL (opcional, si necesitas conectar a la BD)
POSTGRES_HOST=185.166.39.4
POSTGRES_PORT=30000
POSTGRES_DATABASE=atrapo
POSTGRES_USERNAME=admin
POSTGRES_PASSWORD=tu_password_aqui

# Otros secretos que necesites
RABBITMQ_PASSWORD=guest
OPENAI_API_KEY=tu_key_aqui
# ... etc
```

**⚠️ IMPORTANTE**: 
- **NO** commitees el archivo `.env` a Git
- Agrega `.env` a `.gitignore`

#### Paso 2: Instalar DotNetEnv

```bash
# En la terminal, desde la carpeta del proyecto
dotnet add package DotNetEnv
```

#### Paso 3: Cargar el archivo .env en Program.cs

Agrega esto **al inicio** de `Program.cs`, justo después de `var builder = WebApplication.CreateBuilder(args);`:

```csharp
var builder = WebApplication.CreateBuilder(args);

// 🔧 CARGAR VARIABLES DE ENTORNO DESDE .env (SOLO EN DESARROLLO)
if (builder.Environment.IsDevelopment())
{
    var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    if (File.Exists(envPath))
    {
        DotNetEnv.Env.Load(envPath);
        Console.WriteLine($"✅ Archivo .env cargado desde: {envPath}");
    }
    else
    {
        Console.WriteLine($"⚠️ Archivo .env no encontrado en: {envPath}");
    }
}
```

**Nota**: Si usas una ruta diferente, ajusta `envPath` según tu estructura.

---

### Opción 2: Obtener secretos desde Google Cloud Secret Manager

#### Paso 1: Configurar credenciales de Google Cloud

1. Descarga el archivo de credenciales de Google Cloud:
   ```bash
   # Desde tu máquina local
   gcloud auth application-default login
   # O descarga el archivo JSON de credenciales desde GCP Console
   ```

2. Configura la variable de entorno:
   ```powershell
   # En PowerShell (Windows)
   $env:GOOGLE_APPLICATION_CREDENTIALS="C:\ruta\a\tu\cloudcredential.json"
   ```

   O en el código (ya está en Program.cs, pero verifica la ruta):
   ```csharp
   // En Program.cs ya existe esto, pero verifica la ruta
   credentialsPath = "C:\\cloudcredential.json";
   ```

#### Paso 2: Verificar que puedes obtener secretos

```bash
# Probar obtener un secreto
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Si funciona, la aplicación debería poder obtener los secretos automáticamente.

---

### Opción 3: Usar User Secrets de .NET (Solo para desarrollo)

#### Paso 1: Inicializar User Secrets

```bash
# Desde la carpeta del proyecto
dotnet user-secrets init
```

#### Paso 2: Agregar JWT_KEY

```bash
dotnet user-secrets set "Jwt:Key" "ThisIsA32CharacterLongSecretKey123456"
dotnet user-secrets set "Jwt:Issuer" "newApi"
dotnet user-secrets set "Jwt:Audience" "newApi"
```

#### Paso 3: Verificar que Program.cs carga User Secrets

El código ya debería cargar User Secrets automáticamente (está en la configuración por defecto de ASP.NET Core).

---

### Opción 4: Script para descargar secretos de GCSM y crear .env

Ejecuta el script que creamos anteriormente:

```bash
# Desde tu máquina local (con gcloud configurado)
cd C:\Users\Diego\Downloads\App\App\NewApi
# Copia el script desde el servidor o créalo localmente
./ejemplo-cargar-secretos-desarrollo.sh
```

Esto creará un archivo `.env.local` con todos los secretos de GCSM.

---

## 🚀 Solución Rápida (Recomendada)

**Para solucionar el error inmediatamente:**

1. **Crea un archivo `.env`** en la raíz del proyecto con:
   ```env
   JWT_KEY=ThisIsA32CharacterLongSecretKey12345678901234567890
   JWT_ISSUER=newApi
   JWT_AUDIENCE=newApi
   ```

2. **Instala DotNetEnv**:
   ```bash
   dotnet add package DotNetEnv
   ```

3. **Agrega al inicio de Program.cs** (después de `var builder = ...`):
   ```csharp
   // Cargar .env en desarrollo
   if (builder.Environment.IsDevelopment())
   {
       var envFile = Path.Combine(Directory.GetCurrentDirectory(), ".env");
       if (File.Exists(envFile))
       {
           DotNetEnv.Env.Load(envFile);
       }
   }
   ```

4. **Agrega `.env` a `.gitignore`**:
   ```
   .env
   .env.local
   ```

---

## 📝 Verificación

Después de aplicar la solución, verifica que funciona:

1. Ejecuta la aplicación
2. Deberías ver en la consola:
   ```
   ✅ JWT Key length validated: XX bytes (XXX bits) - SECURE
   ```
3. El error "JWT Key not found" no debería aparecer

---

## 🔐 Obtener el JWT_KEY Real de GCSM

Si quieres usar el mismo JWT_KEY que en producción:

```bash
# Desde tu máquina local (con gcloud configurado)
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Copia el valor y úsalo en tu archivo `.env`.

---

## ⚠️ Importante

- **NUNCA** commitees archivos `.env` con secretos reales
- **NUNCA** uses secretos de producción en desarrollo local (a menos que sea necesario)
- Usa valores de prueba/dummy para desarrollo local
- El archivo `.env` debe estar en `.gitignore`

---

¿Necesitas ayuda con alguna de estas opciones? Indica cuál prefieres y te ayudo a implementarla.

