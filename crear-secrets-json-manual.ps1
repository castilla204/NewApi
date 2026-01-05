# Script para crear secrets.json manualmente
# Copia los valores desde el panel de Google Cloud Secret Manager

Write-Host "=== Crear secrets.json desde Panel de Google Cloud ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Instrucciones:" -ForegroundColor Yellow
Write-Host "1. Ve a: https://console.cloud.google.com/security/secret-manager?project=grup-441318" -ForegroundColor Gray
Write-Host "2. Copia cada secreto desde el panel" -ForegroundColor Gray
Write-Host "3. Pega el valor cuando se te solicite" -ForegroundColor Gray
Write-Host ""

$userSecretsPath = "C:\Users\admin\.microsoft\usersecrets\dec0adc1-b7d7-4da6-be0f-42e3054c640a"
$secretsJsonPath = Join-Path $userSecretsPath "secrets.json"

# Crear directorio si no existe
if (-not (Test-Path $userSecretsPath)) {
    Write-Host "📁 Creando directorio: $userSecretsPath" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $userSecretsPath -Force | Out-Null
}

$secrets = @{}

Write-Host "Ingresa los valores de los secretos (presiona Enter para omitir):" -ForegroundColor Cyan
Write-Host ""

# JWT Configuration
Write-Host "🔐 JWT Configuration:" -ForegroundColor Yellow
$jwtKey = Read-Host "  JWT Key (jwt-key)"
if ($jwtKey) { $secrets["Jwt:Key"] = $jwtKey }

$jwtIssuer = Read-Host "  JWT Issuer (jwt-issuer)"
if ($jwtIssuer) { $secrets["Jwt:Issuer"] = $jwtIssuer }

$jwtAudience = Read-Host "  JWT Audience (jwt-audience)"
if ($jwtAudience) { $secrets["Jwt:Audience"] = $jwtAudience }

Write-Host ""

# PostgreSQL
Write-Host "🗄️ PostgreSQL:" -ForegroundColor Yellow
$postgresHost = Read-Host "  Host (postgres-host)"
$postgresPort = Read-Host "  Port (postgres-port)"
$postgresUsername = Read-Host "  Username (postgres-username)"
$postgresPassword = Read-Host "  Password (postgres-password)" -AsSecureString
$postgresPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($postgresPassword))
$postgresDatabase = Read-Host "  Database (postgres-database)"

if ($postgresHost -and $postgresPort -and $postgresUsername -and $postgresPasswordPlain -and $postgresDatabase) {
    $connectionString = "Host=$postgresHost;Port=$postgresPort;Username=$postgresUsername;Password=$postgresPasswordPlain;Database=$postgresDatabase"
    $secrets["ConnectionStrings:PostgresConnection"] = $connectionString
}

if ($postgresHost) { $secrets["Postgres:Host"] = $postgresHost }
if ($postgresPort) { $secrets["Postgres:Port"] = $postgresPort }
if ($postgresUsername) { $secrets["Postgres:Username"] = $postgresUsername }
if ($postgresPasswordPlain) { $secrets["Postgres:Password"] = $postgresPasswordPlain }
if ($postgresDatabase) { $secrets["Postgres:Database"] = $postgresDatabase }

Write-Host ""

# RabbitMQ
Write-Host "🐰 RabbitMQ:" -ForegroundColor Yellow
$rabbitmqPassword = Read-Host "  Password (rabbitmq-password)" -AsSecureString
$rabbitmqPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($rabbitmqPassword))
if ($rabbitmqPasswordPlain) { $secrets["RabbitMQ:Password"] = $rabbitmqPasswordPlain }

Write-Host ""

# OpenAI
Write-Host "🤖 OpenAI:" -ForegroundColor Yellow
$openaiApiKey = Read-Host "  API Key (openai-api-key)"
if ($openaiApiKey) { $secrets["OpenAI:ApiKey"] = $openaiApiKey }

Write-Host ""

# Google OAuth
Write-Host "🔵 Google OAuth:" -ForegroundColor Yellow
$googleClientIds = Read-Host "  Client IDs (google-client-ids)"
if ($googleClientIds) { $secrets["Google:ClientIds"] = $googleClientIds }

# Google Maps
$googleMapsApiKey = Read-Host "  Maps API Key (google-maps-api-key)"
if ($googleMapsApiKey) { $secrets["GoogleMaps:ApiKey"] = $googleMapsApiKey }

Write-Host ""

# Email SMTP
Write-Host "📧 Email SMTP:" -ForegroundColor Yellow
$emailFromEmail = Read-Host "  From Email (email-from-email)"
if ($emailFromEmail) { $secrets["Email:FromEmail"] = $emailFromEmail }

$emailFromName = Read-Host "  From Name (email-from-name)"
if ($emailFromName) { $secrets["Email:FromName"] = $emailFromName }

$emailSmtpHost = Read-Host "  SMTP Host (email-smtp-host)"
if ($emailSmtpHost) { $secrets["Email:SmtpHost"] = $emailSmtpHost }

$emailSmtpPort = Read-Host "  SMTP Port (email-smtp-port)"
if ($emailSmtpPort) { $secrets["Email:SmtpPort"] = $emailSmtpPort }

$emailSmtpUsername = Read-Host "  SMTP Username (email-smtp-username)"
if ($emailSmtpUsername) { $secrets["Email:SmtpUsername"] = $emailSmtpUsername }

$emailSmtpPassword = Read-Host "  SMTP Password (email-smtp-password)" -AsSecureString
$emailSmtpPasswordPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($emailSmtpPassword))
if ($emailSmtpPasswordPlain) { $secrets["Email:SmtpPassword"] = $emailSmtpPasswordPlain }

Write-Host ""

# Stripe
Write-Host "💳 Stripe:" -ForegroundColor Yellow
$stripeSecretKey = Read-Host "  Secret Key (stripe-secret-key)"
if ($stripeSecretKey) { $secrets["Stripe:SecretKey"] = $stripeSecretKey }

$stripeWebhookSecret = Read-Host "  Webhook Secret (stripe-webhook-secret)"
if ($stripeWebhookSecret) { $secrets["Stripe:WebhookSecret"] = $stripeWebhookSecret }

$stripeGeneralWebhookSecret = Read-Host "  General Webhook Secret (stripe-general-webhook-secret)"
if ($stripeGeneralWebhookSecret) { $secrets["Stripe:GeneralWebhookSecret"] = $stripeGeneralWebhookSecret }

Write-Host ""

# Twilio
Write-Host "📱 Twilio:" -ForegroundColor Yellow
$twilioAccountSid = Read-Host "  Account SID (twilio-account-sid)"
if ($twilioAccountSid) { $secrets["Twilio:AccountSid"] = $twilioAccountSid }

$twilioAuthToken = Read-Host "  Auth Token (twilio-auth-token)" -AsSecureString
$twilioAuthTokenPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($twilioAuthToken))
if ($twilioAuthTokenPlain) { $secrets["Twilio:AuthToken"] = $twilioAuthTokenPlain }

$twilioVerificationServiceSid = Read-Host "  Verification Service SID (twilio-verification-service-sid)"
if ($twilioVerificationServiceSid) { $secrets["Twilio:VerificationServiceSid"] = $twilioVerificationServiceSid }

Write-Host ""

# MFA Encryption Key
Write-Host "🔒 MFA:" -ForegroundColor Yellow
$mfaEncryptionKey = Read-Host "  Encryption Key (mfa-encryption-key)"
if ($mfaEncryptionKey) { $secrets["MFA:EncryptionKey"] = $mfaEncryptionKey }

Write-Host ""

# Redis
Write-Host "🔴 Redis:" -ForegroundColor Yellow
$redisConnectionString = Read-Host "  Connection String (redis-connection-string)"
if ($redisConnectionString) { $secrets["Redis:ConnectionString"] = $redisConnectionString }

Write-Host ""

# Convertir a JSON y guardar
Write-Host "💾 Guardando secretos en: $secretsJsonPath" -ForegroundColor Cyan

$jsonContent = $secrets | ConvertTo-Json -Depth 10
$jsonContent | Out-File -FilePath $secretsJsonPath -Encoding utf8 -NoNewline

Write-Host ""
Write-Host "✅ Archivo secrets.json creado exitosamente!" -ForegroundColor Green
Write-Host "   Ubicación: $secretsJsonPath" -ForegroundColor Yellow
Write-Host "   Secretos configurados: $($secrets.Count)" -ForegroundColor Yellow
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

















