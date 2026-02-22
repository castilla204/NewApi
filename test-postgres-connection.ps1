# Script para probar conexión a PostgreSQL desde PowerShell
# Usa Npgsql directamente para verificar la conexión

$host = "localhost"
$port = 5435
$username = "admin"
$password = $env:PGPASSWORD
if ([string]::IsNullOrWhiteSpace($password)) {
    Write-Host "Define PGPASSWORD antes de ejecutar este script." -ForegroundColor Red
    exit 1
}
$database = "atrapo"

Write-Host "=== Probando conexión a PostgreSQL ===" -ForegroundColor Cyan
Write-Host "Host: $host"
Write-Host "Port: $port"
Write-Host "Username: $username"
Write-Host "Database: $database"
Write-Host "Password: $($password.Substring(0, [Math]::Min(3, $password.Length)))***"
Write-Host ""

# Construir connection string
$connectionString = "Host=$host;Port=$port;Username=$username;Password=$password;Database=$database;Timeout=5;"

Write-Host "Connection String: Host=$host;Port=$port;Username=$username;Password=***;Database=$database;Timeout=5;"
Write-Host ""

try {
    # Cargar Npgsql si está disponible
    $npgsqlPath = Get-ChildItem -Path ".\bin\Debug\net8.0" -Filter "Npgsql.dll" -Recurse | Select-Object -First 1
    
    if ($npgsqlPath) {
        Write-Host "Cargando Npgsql desde: $($npgsqlPath.FullName)" -ForegroundColor Yellow
        Add-Type -Path $npgsqlPath.FullName
        
        $conn = New-Object Npgsql.NpgsqlConnection($connectionString)
        Write-Host "Intentando conectar..." -ForegroundColor Yellow
        $conn.Open()
        Write-Host "✅ CONEXIÓN EXITOSA!" -ForegroundColor Green
        
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT version();"
        $reader = $cmd.ExecuteReader()
        if ($reader.Read()) {
            Write-Host "Versión PostgreSQL: $($reader[0])" -ForegroundColor Green
        }
        $reader.Close()
        $conn.Close()
    } else {
        Write-Host "⚠️ Npgsql.dll no encontrado. Compila el proyecto primero." -ForegroundColor Yellow
        Write-Host "Connection string que se usaría:" -ForegroundColor Yellow
        Write-Host $connectionString
    }
} catch {
    Write-Host "❌ ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Tipo: $($_.Exception.GetType().Name)" -ForegroundColor Red
}

