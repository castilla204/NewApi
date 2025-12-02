# Script PowerShell para configurar desarrollo local en Windows
# Ejecutar desde la carpeta del proyecto NewApi

Write-Host "=== Configuración de Desarrollo Local ===" -ForegroundColor Cyan
Write-Host ""

$projectPath = $PSScriptRoot
if ([string]::IsNullOrEmpty($projectPath)) {
    $projectPath = Get-Location
}

Write-Host "Directorio del proyecto: $projectPath" -ForegroundColor Yellow
Write-Host ""

# Verificar si gcloud está instalado
$gcloudAvailable = $false
try {
    $gcloudVersion = gcloud --version 2>&1
    if ($LASTEXITCODE -eq 0) {
        $gcloudAvailable = $true
        Write-Host "✅ gcloud CLI encontrado" -ForegroundColor Green
    }
} catch {
    Write-Host "⚠️ gcloud CLI no encontrado" -ForegroundColor Yellow
}

# Verificar autenticación de gcloud
$gcloudAuthenticated = $false
if ($gcloudAvailable) {
    try {
        $authList = gcloud auth list --filter=status:ACTIVE --format="value(account)" 2>&1
        if ($authList -and $LASTEXITCODE -eq 0) {
            $gcloudAuthenticated = $true
            Write-Host "✅ Autenticado en gcloud como: $authList" -ForegroundColor Green
        }
    } catch {
        Write-Host "⚠️ No autenticado en gcloud" -ForegroundColor Yellow
    }
}

$projectId = "grup-441318"
$envFile = Join-Path $projectPath ".env"

Write-Host ""
Write-Host "¿Cómo quieres configurar los secretos?" -ForegroundColor Cyan
Write-Host "1. Descargar desde Google Cloud Secret Manager (requiere gcloud configurado)"
Write-Host "2. Crear archivo .env con valores de ejemplo (para desarrollo)"
Write-Host "3. Solo verificar configuración actual"
Write-Host ""
$choice = Read-Host "Selecciona una opción (1-3)"

if ($choice -eq "1") {
    if (-not $gcloudAuthenticated) {
        Write-Host "❌ Error: Necesitas estar autenticado en gcloud" -ForegroundColor Red
        Write-Host "Ejecuta: gcloud auth login" -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host ""
    Write-Host "Descargando secretos desde Google Cloud Secret Manager..." -ForegroundColor Cyan
    
    # Función para obtener secreto
    function Get-GCPSecret {
        param([string]$secretName)
        try {
            $value = gcloud secrets versions access latest --secret=$secretName --project=$projectId 2>&1
            if ($LASTEXITCODE -eq 0) {
                return $value.Trim()
            }
        } catch {
            Write-Host "⚠️ No se pudo obtener: $secretName" -ForegroundColor Yellow
        }
        return $null
    }
    
    # Crear archivo .env
    @"
# Archivo generado automáticamente desde Google Cloud Secret Manager
# NO COMMITEAR ESTE ARCHIVO
# Generado el: $(Get-Date)

# JWT Configuration
JWT_KEY=$(Get-GCPSecret "jwt-key")
JWT_ISSUER=$(Get-GCPSecret "jwt-issuer")
JWT_AUDIENCE=$(Get-GCPSecret "jwt-audience")

# PostgreSQL
POSTGRES_HOST=$(Get-GCPSecret "postgres-host")
POSTGRES_PORT=$(Get-GCPSecret "postgres-port")
POSTGRES_USERNAME=$(Get-GCPSecret "postgres-username")
POSTGRES_PASSWORD=$(Get-GCPSecret "postgres-password")
POSTGRES_DATABASE=$(Get-GCPSecret "postgres-database")

# RabbitMQ
RABBITMQ_PASSWORD=$(Get-GCPSecret "rabbitmq-password")

# OpenAI
OPENAI_API_KEY=$(Get-GCPSecret "openai-api-key")

# Google OAuth
GOOGLE_CLIENT_IDS=$(Get-GCPSecret "google-client-ids")

# Email SMTP
EMAIL_FROM_EMAIL=$(Get-GCPSecret "email-from-email")
EMAIL_FROM_NAME=$(Get-GCPSecret "email-from-name")
EMAIL_SMTP_HOST=$(Get-GCPSecret "email-smtp-host")
EMAIL_SMTP_PORT=$(Get-GCPSecret "email-smtp-port")
EMAIL_SMTP_USERNAME=$(Get-GCPSecret "email-smtp-username")
EMAIL_SMTP_PASSWORD=$(Get-GCPSecret "email-smtp-password")

# Stripe
STRIPE_SECRET_KEY=$(Get-GCPSecret "stripe-secret-key")
STRIPE_WEBHOOK_SECRET=$(Get-GCPSecret "stripe-webhook-secret")

# Twilio
TWILIO_ACCOUNT_SID=$(Get-GCPSecret "twilio-account-sid")
TWILIO_AUTH_TOKEN=$(Get-GCPSecret "twilio-auth-token")
TWILIO_VERIFICATION_SERVICE_SID=$(Get-GCPSecret "twilio-verification-service-sid")
"@ | Out-File -FilePath $envFile -Encoding utf8
    
    Write-Host "✅ Archivo .env creado en: $envFile" -ForegroundColor Green
    
} elseif ($choice -eq "2") {
    Write-Host ""
    Write-Host "Creando archivo .env con valores de ejemplo..." -ForegroundColor Cyan
    
    @"
# Archivo de configuración para desarrollo local
# NO COMMITEAR ESTE ARCHIVO
# Reemplaza los valores con tus secretos reales

# JWT Configuration (MÍNIMO 32 caracteres para seguridad)
JWT_KEY=ThisIsA32CharacterLongSecretKey12345678901234567890
JWT_ISSUER=newApi
JWT_AUDIENCE=newApi

# PostgreSQL (opcional - solo si necesitas conectar a la BD)
POSTGRES_HOST=185.166.39.4
POSTGRES_PORT=30000
POSTGRES_USERNAME=admin
POSTGRES_PASSWORD=tu_password_aqui
POSTGRES_DATABASE=atrapo

# RabbitMQ
RABBITMQ_PASSWORD=guest

# OpenAI (opcional)
OPENAI_API_KEY=tu_openai_key_aqui

# Google OAuth (opcional)
GOOGLE_CLIENT_IDS=["61603823707-4vsp43naifci8t893hdc276kkhbvn49a.apps.googleusercontent.com"]

# Email SMTP (opcional)
EMAIL_FROM_EMAIL=info@inspecciono.com
EMAIL_FROM_NAME=Inspecciono
EMAIL_SMTP_HOST=smtp.hostinger.com
EMAIL_SMTP_PORT=587
EMAIL_SMTP_USERNAME=info@inspecciono.com
EMAIL_SMTP_PASSWORD=tu_password_smtp_aqui

# Stripe (opcional)
STRIPE_SECRET_KEY=sk_test_tu_key_aqui
STRIPE_WEBHOOK_SECRET=whsec_tu_webhook_secret_aqui

# Twilio (opcional)
TWILIO_ACCOUNT_SID=tu_account_sid_aqui
TWILIO_AUTH_TOKEN=tu_auth_token_aqui
TWILIO_VERIFICATION_SERVICE_SID=tu_service_sid_aqui
"@ | Out-File -FilePath $envFile -Encoding utf8
    
    Write-Host "✅ Archivo .env creado en: $envFile" -ForegroundColor Green
    Write-Host "⚠️ IMPORTANTE: Edita el archivo y reemplaza los valores de ejemplo con tus secretos reales" -ForegroundColor Yellow
    
} else {
    Write-Host ""
    Write-Host "Verificando configuración actual..." -ForegroundColor Cyan
}

# Verificar si existe .env
if (Test-Path $envFile) {
    Write-Host ""
    Write-Host "✅ Archivo .env encontrado: $envFile" -ForegroundColor Green
    
    # Verificar si tiene JWT_KEY
    $envContent = Get-Content $envFile -Raw
    if ($envContent -match "JWT_KEY=") {
        Write-Host "✅ JWT_KEY encontrado en .env" -ForegroundColor Green
    } else {
        Write-Host "⚠️ JWT_KEY no encontrado en .env" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "❌ Archivo .env no encontrado" -ForegroundColor Red
    Write-Host "   Crea uno usando la opción 1 o 2" -ForegroundColor Yellow
}

# Verificar DotNetEnv
Write-Host ""
Write-Host "Verificando paquete DotNetEnv..." -ForegroundColor Cyan
$csprojFile = Get-ChildItem -Path $projectPath -Filter "*.csproj" | Select-Object -First 1
if ($csprojFile) {
    $csprojContent = Get-Content $csprojFile.FullName -Raw
    if ($csprojContent -match "DotNetEnv") {
        Write-Host "✅ DotNetEnv está instalado" -ForegroundColor Green
    } else {
        Write-Host "⚠️ DotNetEnv NO está instalado" -ForegroundColor Yellow
        Write-Host "   Instálalo con: dotnet add package DotNetEnv" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️ No se encontró archivo .csproj" -ForegroundColor Yellow
}

# Verificar .gitignore
Write-Host ""
Write-Host "Verificando .gitignore..." -ForegroundColor Cyan
$gitignoreFile = Join-Path $projectPath ".gitignore"
if (Test-Path $gitignoreFile) {
    $gitignoreContent = Get-Content $gitignoreFile -Raw
    if ($gitignoreContent -match "\.env") {
        Write-Host "✅ .env está en .gitignore" -ForegroundColor Green
    } else {
        Write-Host "⚠️ .env NO está en .gitignore" -ForegroundColor Yellow
        Write-Host "   Agrega '.env' a .gitignore para evitar commitear secretos" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️ .gitignore no encontrado" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Configuración completada ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Próximos pasos:" -ForegroundColor Yellow
Write-Host "1. Asegúrate de tener DotNetEnv instalado: dotnet add package DotNetEnv"
Write-Host "2. Agrega código para cargar .env en Program.cs (ver SOLUCION_DESARROLLO_LOCAL.md)"
Write-Host "3. Ejecuta la aplicación y verifica que funciona"
Write-Host ""

