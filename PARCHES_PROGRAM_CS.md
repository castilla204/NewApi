# 🔧 Parches para Program.cs - Cargar .env en Desarrollo

## Problema
La aplicación no encuentra `JWT_KEY` en desarrollo local porque no hay variables de entorno configuradas.

## Solución: Cargar archivo .env

### Paso 1: Instalar DotNetEnv

```bash
# Desde la carpeta del proyecto
dotnet add package DotNetEnv
```

### Paso 2: Agregar using al inicio de Program.cs

Agrega esta línea en la sección de `using` al inicio del archivo:

```csharp
using DotNetEnv;  // Agregar esta línea
```

### Paso 3: Cargar .env justo después de crear el builder

Busca esta línea en `Program.cs`:
```csharp
var builder = WebApplication.CreateBuilder(args);
```

Y **inmediatamente después**, agrega este código:

```csharp
var builder = WebApplication.CreateBuilder(args);

// 🔧 CARGAR VARIABLES DE ENTORNO DESDE .env (SOLO EN DESARROLLO)
if (builder.Environment.IsDevelopment())
{
    // Buscar archivo .env en varias ubicaciones posibles
    var possibleEnvPaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env"),
        Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? "", ".env"),
        ".env"  // Ruta relativa
    };
    
    bool envLoaded = false;
    foreach (var envPath in possibleEnvPaths)
    {
        if (File.Exists(envPath))
        {
            try
            {
                Env.Load(envPath);
                Console.WriteLine($"✅ Archivo .env cargado desde: {Path.GetFullPath(envPath)}");
                envLoaded = true;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error al cargar .env desde {envPath}: {ex.Message}");
            }
        }
    }
    
    if (!envLoaded)
    {
        Console.WriteLine($"⚠️ Archivo .env no encontrado. Buscado en:");
        foreach (var path in possibleEnvPaths)
        {
            Console.WriteLine($"   - {Path.GetFullPath(path)}");
        }
        Console.WriteLine("   Crea un archivo .env con JWT_KEY y otros secretos necesarios.");
    }
}
```

### Código Completo del Parche

Aquí está el código completo que debes agregar:

```csharp
using DotNetEnv;  // ← Agregar este using

// ... otros usings ...

var builder = WebApplication.CreateBuilder(args);

// 🔧 CARGAR VARIABLES DE ENTORNO DESDE .env (SOLO EN DESARROLLO)
if (builder.Environment.IsDevelopment())
{
    var possibleEnvPaths = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env"),
        ".env"
    };
    
    bool envLoaded = false;
    foreach (var envPath in possibleEnvPaths)
    {
        if (File.Exists(envPath))
        {
            try
            {
                Env.Load(envPath);
                Console.WriteLine($"✅ Archivo .env cargado desde: {Path.GetFullPath(envPath)}");
                envLoaded = true;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error al cargar .env: {ex.Message}");
            }
        }
    }
    
    if (!envLoaded)
    {
        Console.WriteLine("⚠️ Archivo .env no encontrado. Crea uno con JWT_KEY y otros secretos.");
    }
}

// Configurar logging básico
builder.Logging.ClearProviders();
// ... resto del código ...
```

---

## 📝 Ejemplo de archivo .env

Crea un archivo `.env` en la raíz del proyecto con:

```env
# JWT Configuration (MÍNIMO 32 caracteres)
JWT_KEY=ThisIsA32CharacterLongSecretKey12345678901234567890
JWT_ISSUER=newApi
JWT_AUDIENCE=newApi

# PostgreSQL (opcional)
POSTGRES_HOST=185.166.39.4
POSTGRES_PORT=30000
POSTGRES_USERNAME=admin
POSTGRES_PASSWORD=tu_password
POSTGRES_DATABASE=atrapo

# Otros secretos según necesites
RABBITMQ_PASSWORD=guest
OPENAI_API_KEY=tu_key
```

---

## ✅ Verificación

Después de aplicar el parche:

1. Crea el archivo `.env` con `JWT_KEY`
2. Ejecuta la aplicación
3. Deberías ver en la consola:
   ```
   ✅ Archivo .env cargado desde: C:\Users\Diego\Downloads\App\App\NewApi\.env
   ✅ JWT Key length validated: XX bytes (XXX bits) - SECURE
   ```
4. El error "JWT Key not found" no debería aparecer

---

## 🔐 Obtener JWT_KEY Real de Producción

Si quieres usar el mismo JWT_KEY que en producción:

```bash
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

Copia el valor y úsalo en tu `.env`.

---

## ⚠️ Importante

- **NO** commitees el archivo `.env` a Git
- Agrega `.env` a `.gitignore`
- Usa valores de prueba para desarrollo local (no uses secretos de producción)

