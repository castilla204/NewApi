# Configuración de Túnel de Base de Datos para Desarrollo

## Resumen

En **desarrollo**, la aplicación ahora usa configuraciones locales para conectar a la base de datos a través de un túnel (Cloud SQL Proxy, SSH tunnel, etc.), en lugar de usar Google Cloud Secret Manager.

En **producción**, la aplicación sigue usando Google Cloud Secret Manager para obtener las credenciales de forma segura.

## Configuración en Desarrollo

Tienes **3 opciones** para configurar la conexión a la base de datos en desarrollo:

### Opción 1: User Secrets (RECOMENDADO - Más Seguro)

```bash
cd C:\Users\Diego\OneDrive - Educacyl\Escritorio\App\newApi
dotnet user-secrets set "ConnectionStrings:PostgresConnection" "Host=localhost;Port=5432;Username=postgres;Password=TU_PASSWORD;Database=newapi;Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;"
```

### Opción 2: Variables de Entorno

```powershell
# PowerShell
$env:DB_HOST = "localhost"
$env:DB_PORT = "5432"
$env:DB_USERNAME = "postgres"
$env:DB_PASSWORD = "TU_PASSWORD"
$env:DB_NAME = "newapi"
```

```bash
# Bash (Git Bash, WSL, Linux)
export DB_HOST="localhost"
export DB_PORT="5432"
export DB_USERNAME="postgres"
export DB_PASSWORD="TU_PASSWORD"
export DB_NAME="newapi"
```

### Opción 3: Modificar appsettings.Development.json

**⚠️ ADVERTENCIA:** No subas este archivo con credenciales reales a Git.

Edita `appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "PostgresConnection": "Host=localhost;Port=5432;Username=postgres;Password=TU_PASSWORD;Database=newapi;Timeout=30;CommandTimeout=30;ConnectionIdleLifetime=300;ConnectionPruningInterval=10;"
  }
}
```

## Configuración del Túnel

### Cloud SQL Proxy (Google Cloud)

1. Instala Cloud SQL Proxy:
   ```bash
   curl -o cloud-sql-proxy https://storage.googleapis.com/cloud-sql-connectors/cloud-sql-proxy/v2.8.0/cloud-sql-proxy.x64.exe
   ```

2. Ejecuta el proxy:
   ```bash
   ./cloud-sql-proxy --port 5432 grup-441318:europe-west1:INSTANCE_NAME
   ```

3. Ahora tu aplicación se conectará a `localhost:5432` que reenviará al Cloud SQL.

### SSH Tunnel

```bash
ssh -L 5432:localhost:5432 usuario@servidor-remoto
```

### Kubernetes Port Forward

```bash
kubectl port-forward svc/postgres-service 5432:5432 -n default
```

## Verificar la Configuración

```bash
dotnet run --environment Development
```

Si todo está bien, verás logs indicando que la aplicación se conectó correctamente a la base de datos.

## Producción

En producción, **NO necesitas hacer nada**. La aplicación automáticamente usará Google Cloud Secret Manager para obtener:

- `postgres-host`
- `postgres-port`
- `postgres-username`
- `postgres-password`
- `postgres-database`

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

