# Script para descargar secretos desde Google Cloud Secret Manager
# y crear el archivo secrets.json en la ruta de User Secrets

Write-Host "=== Descargar Secretos desde Google Cloud Secret Manager ===" -ForegroundColor Cyan
Write-Host ""

$projectId = "grup-441318"
$userSecretsPath = "C:\Users\admin\.microsoft\usersecrets\dec0adc1-b7d7-4da6-be0f-42e3054c640a"
$secretsJsonPath = Join-Path $userSecretsPath "secrets.json"

# Verificar si gcloud está instalado
$gcloudAvailable = $false
try {
    $gcloudVersion = gcloud --version 2>&1
    if ($LASTEXITCODE -eq 0) {
        $gcloudAvailable = $true
        Write-Host "✅ gcloud CLI encontrado" -ForegroundColor Green
    }
} catch {
    Write-Host "❌ gcloud CLI no encontrado" -ForegroundColor Red
    Write-Host "   Instala Google Cloud SDK desde: https://cloud.google.com/sdk/docs/install" -ForegroundColor Yellow
    exit 1
}

# Verificar autenticación
if ($gcloudAvailable) {
    try {
        $authList = gcloud auth list --filter=status:ACTIVE --format="value(account)" 2>&1
        if ($authList -and $LASTEXITCODE -eq 0) {
            Write-Host "✅ Autenticado en gcloud como: $authList" -ForegroundColor Green
        } else {
            Write-Host "❌ No autenticado en gcloud" -ForegroundColor Red
            Write-Host "   Ejecuta: gcloud auth login" -ForegroundColor Yellow
            exit 1
        }
    } catch {
        Write-Host "❌ Error verificando autenticación" -ForegroundColor Red
        exit 1
    }
}

# Crear directorio si no existe
if (-not (Test-Path $userSecretsPath)) {
    Write-Host "📁 Creando directorio: $userSecretsPath" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $userSecretsPath -Force | Out-Null
}

Write-Host ""
Write-Host "📥 Descargando secretos desde Google Cloud Secret Manager..." -ForegroundColor Cyan
Write-Host "   Proyecto: $projectId" -ForegroundColor Yellow
Write-Host ""

# Función para obtener secreto
function Get-GCPSecret {
    param([string]$secretName)
    try {
        Write-Host "   📦 Obteniendo: $secretName" -ForegroundColor Gray
        $value = gcloud secrets versions access latest --secret=$secretName --project=$projectId 2>&1
        if ($LASTEXITCODE -eq 0) {
            return $value.Trim()
        } else {
            Write-Host "   ⚠️ No se pudo obtener: $secretName" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "   ⚠️ Error obteniendo: $secretName" -ForegroundColor Yellow
    }
    return $null
}

# Crear objeto JSON con los secretos
$secrets = @{}

# JWT Configuration
$jwtKey = Get-GCPSecret "jwt-key"
if ($jwtKey) { $secrets["Jwt:Key"] = $jwtKey }

$jwtIssuer = Get-GCPSecret "jwt-issuer"
if ($jwtIssuer) { $secrets["Jwt:Issuer"] = $jwtIssuer }

$jwtAudience = Get-GCPSecret "jwt-audience"
if ($jwtAudience) { $secrets["Jwt:Audience"] = $jwtAudience }

# PostgreSQL Connection String
$postgresHost = Get-GCPSecret "postgres-host"
$postgresPort = Get-GCPSecret "postgres-port"
$postgresUsername = Get-GCPSecret "postgres-username"
$postgresPassword = Get-GCPSecret "postgres-password"
$postgresDatabase = Get-GCPSecret "postgres-database"

if ($postgresHost -and $postgresPort -and $postgresUsername -and $postgresPassword -and $postgresDatabase) {
    $connectionString = "Host=$postgresHost;Port=$postgresPort;Username=$postgresUsername;Password=$postgresPassword;Database=$postgresDatabase"
    $secrets["ConnectionStrings:PostgresConnection"] = $connectionString
}

# PostgreSQL individual (por si se necesitan)
if ($postgresHost) { $secrets["Postgres:Host"] = $postgresHost }
if ($postgresPort) { $secrets["Postgres:Port"] = $postgresPort }
if ($postgresUsername) { $secrets["Postgres:Username"] = $postgresUsername }
if ($postgresPassword) { $secrets["Postgres:Password"] = $postgresPassword }
if ($postgresDatabase) { $secrets["Postgres:Database"] = $postgresDatabase }

# RabbitMQ
$rabbitmqPassword = Get-GCPSecret "rabbitmq-password"
if ($rabbitmqPassword) { $secrets["RabbitMQ:Password"] = $rabbitmqPassword }

# OpenAI
$openaiApiKey = Get-GCPSecret "openai-api-key"
if ($openaiApiKey) { $secrets["OpenAI:ApiKey"] = $openaiApiKey }

# Google OAuth
$googleClientIds = Get-GCPSecret "google-client-ids"
if ($googleClientIds) { $secrets["Google:ClientIds"] = $googleClientIds }

# Google Maps
$googleMapsApiKey = Get-GCPSecret "google-maps-api-key"
if ($googleMapsApiKey) { $secrets["GoogleMaps:ApiKey"] = $googleMapsApiKey }

# Email SMTP
$emailFromEmail = Get-GCPSecret "email-from-email"
if ($emailFromEmail) { $secrets["Email:FromEmail"] = $emailFromEmail }

$emailFromName = Get-GCPSecret "email-from-name"
if ($emailFromName) { $secrets["Email:FromName"] = $emailFromName }

$emailSmtpHost = Get-GCPSecret "email-smtp-host"
if ($emailSmtpHost) { $secrets["Email:SmtpHost"] = $emailSmtpHost }

$emailSmtpPort = Get-GCPSecret "email-smtp-port"
if ($emailSmtpPort) { $secrets["Email:SmtpPort"] = $emailSmtpPort }

$emailSmtpUsername = Get-GCPSecret "email-smtp-username"
if ($emailSmtpUsername) { $secrets["Email:SmtpUsername"] = $emailSmtpUsername }

$emailSmtpPassword = Get-GCPSecret "email-smtp-password"
if ($emailSmtpPassword) { $secrets["Email:SmtpPassword"] = $emailSmtpPassword }

# Stripe
$stripeSecretKey = Get-GCPSecret "stripe-secret-key"
if ($stripeSecretKey) { $secrets["Stripe:SecretKey"] = $stripeSecretKey }

$stripeWebhookSecret = Get-GCPSecret "stripe-webhook-secret"
if ($stripeWebhookSecret) { $secrets["Stripe:WebhookSecret"] = $stripeWebhookSecret }

$stripeGeneralWebhookSecret = Get-GCPSecret "stripe-general-webhook-secret"
if ($stripeGeneralWebhookSecret) { $secrets["Stripe:GeneralWebhookSecret"] = $stripeGeneralWebhookSecret }

# Twilio
$twilioAccountSid = Get-GCPSecret "twilio-account-sid"
if ($twilioAccountSid) { $secrets["Twilio:AccountSid"] = $twilioAccountSid }

$twilioAuthToken = Get-GCPSecret "twilio-auth-token"
if ($twilioAuthToken) { $secrets["Twilio:AuthToken"] = $twilioAuthToken }

$twilioVerificationServiceSid = Get-GCPSecret "twilio-verification-service-sid"
if ($twilioVerificationServiceSid) { $secrets["Twilio:VerificationServiceSid"] = $twilioVerificationServiceSid }

# MFA Encryption Key
$mfaEncryptionKey = Get-GCPSecret "mfa-encryption-key"
if ($mfaEncryptionKey) { $secrets["MFA:EncryptionKey"] = $mfaEncryptionKey }

# Redis
$redisConnectionString = Get-GCPSecret "redis-connection-string"
if ($redisConnectionString) { $secrets["Redis:ConnectionString"] = $redisConnectionString }

# Convertir a JSON y guardar
Write-Host ""
Write-Host "💾 Guardando secretos en: $secretsJsonPath" -ForegroundColor Cyan

$jsonContent = $secrets | ConvertTo-Json -Depth 10
$jsonContent | Out-File -FilePath $secretsJsonPath -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "✅ Archivo secrets.json creado exitosamente!" -ForegroundColor Green
Write-Host "   Ubicación: $secretsJsonPath" -ForegroundColor Yellow
Write-Host "   Secretos descargados: $($secrets.Count)" -ForegroundColor Yellow
Write-Host ""
Write-Host "📋 Secretos configurados:" -ForegroundColor Cyan
foreach ($key in $secrets.Keys | Sort-Object) {
    $value = $secrets[$key]
    $displayValue = if ($value.Length -gt 50) { $value.Substring(0, 50) + "..." } else { $value }
    Write-Host "   ✅ $key = $displayValue" -ForegroundColor Gray
}
Write-Host ""
Write-Host "🎉 ¡Configuración completada! La aplicación ahora puede usar estos secretos." -ForegroundColor Green
Write-Host ""










