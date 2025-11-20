# Configuración de Túnel de Base de Datos para Desarrollo

## Resumen

**IMPORTANTE:** La aplicación usa la **MISMA base de datos** en desarrollo y producción.

- **En desarrollo**: Se conecta a través de un **túnel local** (Cloud SQL Proxy) que redirige `localhost:5432` a la base de datos de producción en Google Cloud. Las credenciales se configuran localmente (User Secrets o variables de entorno).

- **En producción**: Se conecta **directamente** a la base de datos usando credenciales desde Google Cloud Secret Manager.

## Configuración en Desarrollo

Tienes **3 opciones** para configurar la conexión a la base de datos en desarrollo:

### Opción 1: User Secrets (RECOMENDADO - Más Seguro)

**IMPORTANTE:** Usa las mismas credenciales de la base de datos de producción, pero con `Host=localhost` porque el túnel redirige.

```bash
cd C:\Users\Diego\OneDrive - Educacyl\Escritorio\App\newApi
dotnet user-secrets set "ConnectionStrings:PostgresConnection" "Host=localhost;Port=5432;Username=TU_USUARIO_PROD;Password=TU_PASSWORD_PROD;Database=TU_DATABASE_PROD;Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;"
```

### Opción 2: Variables de Entorno

**IMPORTANTE:** Usa las mismas credenciales de producción, pero con `DB_HOST=localhost` porque el túnel redirige.

```powershell
# PowerShell
$env:DB_HOST = "localhost"
$env:DB_PORT = "5432"
$env:DB_USERNAME = "TU_USUARIO_PROD"
$env:DB_PASSWORD = "TU_PASSWORD_PROD"
$env:DB_NAME = "TU_DATABASE_PROD"
```

```bash
# Bash (Git Bash, WSL, Linux)
export DB_HOST="localhost"
export DB_PORT="5432"
export DB_USERNAME="TU_USUARIO_PROD"
export DB_PASSWORD="TU_PASSWORD_PROD"
export DB_NAME="TU_DATABASE_PROD"
```

### Opción 3: Modificar appsettings.Development.json

**⚠️ ADVERTENCIA:** No subas este archivo con credenciales reales a Git. Agrega `appsettings.Development.json` a `.gitignore`.

**IMPORTANTE:** Usa las mismas credenciales de producción, pero con `Host=localhost` porque el túnel redirige.

Edita `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "PostgresConnection": "Host=localhost;Port=5432;Username=TU_USUARIO_PROD;Password=TU_PASSWORD_PROD;Database=TU_DATABASE_PROD;Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;"
  }
}
```

## Configuración del Túnel

**REQUISITO:** Debes tener un túnel activo antes de ejecutar la aplicación en desarrollo.

### Cloud SQL Proxy (Google Cloud) - RECOMENDADO

El Cloud SQL Proxy crea un túnel seguro entre tu máquina local y la base de datos de producción en Google Cloud.

1. Instala Cloud SQL Proxy:
   ```bash
   # Windows
   curl -o cloud-sql-proxy.exe https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/v2.8.0/cloud-sql-proxy.x64.exe
   
   # Linux/Mac
   curl -o cloud-sql-proxy https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/v2.8.0/cloud-sql-proxy.linux.amd64
   chmod +x cloud-sql-proxy
   ```

2. Ejecuta el proxy (reemplaza con tu instance name real):
   ```bash
   # Windows
   .\cloud-sql-proxy.exe --port 5432 grup-441318:europe-west1:TU_INSTANCE_NAME
   
   # Linux/Mac
   ./cloud-sql-proxy --port 5432 grup-441318:europe-west1:TU_INSTANCE_NAME
   ```

3. Deja el proxy ejecutándose en una terminal. Ahora `localhost:5432` redirige a tu base de datos de producción en Cloud SQL.

### SSH Tunnel

```bash
ssh -L 5432:localhost:5432 usuario@servidor-remoto
```

### Kubernetes Port Forward

```bash
kubectl port-forward svc/postgres-service 5432:5432 -n default
```

## Flujo de Trabajo Completo en Desarrollo

1. **Inicia el Cloud SQL Proxy:**
   ```bash
   .\cloud-sql-proxy.exe --port 5432 grup-441318:europe-west1:TU_INSTANCE_NAME
   ```
   
2. **En otra terminal, configura las credenciales** (solo una vez):
   ```bash
   dotnet user-secrets set "ConnectionStrings:PostgresConnection" "Host=localhost;Port=5432;Username=TU_USUARIO;Password=TU_PASSWORD;Database=TU_DATABASE;..."
   ```

3. **Ejecuta la aplicación:**
   ```bash
   dotnet run --environment Development
   ```

4. La aplicación se conecta a `localhost:5432` → Cloud SQL Proxy → Base de datos de producción en Google Cloud

## Verificar la Configuración

```bash
dotnet run --environment Development
```

Si todo está bien, verás logs indicando que la aplicación se conectó correctamente a la base de datos.

## Diagrama de Arquitectura

```
DESARROLLO:
  Tu App (localhost) → localhost:5432 → Cloud SQL Proxy → Google Cloud SQL (Base de datos de producción)
  Credenciales: User Secrets / Variables de Entorno

PRODUCCIÓN:
  Tu App (Kubernetes/GKE) → Directo → Google Cloud SQL (Base de datos de producción)
  Credenciales: Google Cloud Secret Manager
```

## Producción

En producción, **NO necesitas hacer nada**. La aplicación automáticamente usará Google Cloud Secret Manager para obtener:

- `postgres-host` → IP privada de Cloud SQL
- `postgres-port` → 5432
- `postgres-username` → Usuario de producción
- `postgres-password` → Contraseña de producción
- `postgres-database` → Nombre de la base de datos

## Solución de Problemas

### Error: "Cannot connect to database"

1. Verifica que el túnel esté activo
2. Verifica las credenciales en tu configuración
3. Verifica que el puerto sea el correcto (5432 por defecto)

### Error: "Connection timeout"

1. Verifica que el firewall permita conexiones al puerto
2. Verifica que PostgreSQL esté escuchando en localhost

### Error: "Authentication failed"

1. Verifica el username y password
2. Verifica que el usuario tenga permisos en la base de datos

