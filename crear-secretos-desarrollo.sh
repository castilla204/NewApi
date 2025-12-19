#!/bin/bash
# Script para crear secretos de desarrollo en Google Cloud Secret Manager
# Estos secretos tendrán el sufijo -dev para diferenciarlos de producción

set -e

PROJECT_ID="grup-441318"
ENV_SUFFIX="dev"

echo "=== Creando Secretos de Desarrollo en Google Cloud Secret Manager ==="
echo "Proyecto: $PROJECT_ID"
echo "Sufijo: -$ENV_SUFFIX"
echo ""

# Verificar que gcloud está instalado
if ! command -v gcloud &> /dev/null; then
    echo "ERROR: gcloud no está instalado."
    echo "Instálalo desde: https://cloud.google.com/sdk/docs/install"
    exit 1
fi

# Verificar autenticación
if ! gcloud auth list --filter=status:ACTIVE --format="value(account)" | grep -q .; then
    echo "ERROR: No estás autenticado en gcloud."
    echo "Ejecuta: gcloud auth login"
    exit 1
fi

# Lista de secretos a crear (sin el sufijo -dev)
SECRETS=(
    "jwt-key"
    "jwt-issuer"
    "jwt-audience"
    "postgres-host"
    "postgres-port"
    "postgres-username"
    "postgres-password"
    "postgres-database"
    "rabbitmq-password"
    "openai-api-key"
    "google-client-ids"
    "email-from-email"
    "email-from-name"
    "email-smtp-host"
    "email-smtp-port"
    "email-smtp-username"
    "email-smtp-password"
    "stripe-secret-key"
    "stripe-webhook-secret"
    "stripe-general-webhook-secret"
    "twilio-account-sid"
    "twilio-auth-token"
    "twilio-verification-service-sid"
)

echo "Secretos a crear:"
for secret in "${SECRETS[@]}"; do
    echo "  - ${secret}-${ENV_SUFFIX}"
done
echo ""

read -p "¿Continuar? (s/n): " -n 1 -r
echo
if [[ ! $REPLY =~ ^[Ss]$ ]]; then
    echo "Cancelado."
    exit 0
fi

echo ""
echo "Creando secretos..."

for secret in "${SECRETS[@]}"; do
    secret_name="${secret}-${ENV_SUFFIX}"
    
    # Verificar si el secreto ya existe
    if gcloud secrets describe "$secret_name" --project="$PROJECT_ID" &>/dev/null; then
        echo "⚠️  Secreto $secret_name ya existe, omitiendo..."
        continue
    fi
    
    # Crear el secreto
    echo "  Creando: $secret_name"
    
    # Obtener valor del secreto de producción como base (opcional)
    # Si existe el secreto sin sufijo, preguntar si copiar su valor
    if gcloud secrets describe "$secret" --project="$PROJECT_ID" &>/dev/null; then
        read -p "    ¿Copiar valor de $secret? (s/n): " -n 1 -r
        echo
        if [[ $REPLY =~ ^[Ss]$ ]]; then
            echo "    Copiando valor desde $secret..."
            prod_value=$(gcloud secrets versions access latest --secret="$secret" --project="$PROJECT_ID")
            echo -n "$prod_value" | gcloud secrets create "$secret_name" \
                --data-file=- \
                --project="$PROJECT_ID" \
                --replication-policy="automatic" 2>&1 | grep -v "WARNING" || true
            echo "    ✅ $secret_name creado con valor de producción"
        else
            # Crear secreto vacío y pedir valor
            echo "    Ingresa el valor para $secret_name (o presiona Enter para crear vacío):"
            read -s secret_value
            if [ -n "$secret_value" ]; then
                echo -n "$secret_value" | gcloud secrets create "$secret_name" \
                    --data-file=- \
                    --project="$PROJECT_ID" \
                    --replication-policy="automatic" 2>&1 | grep -v "WARNING" || true
                echo "    ✅ $secret_name creado"
            else
                # Crear secreto vacío
                echo "dummy" | gcloud secrets create "$secret_name" \
                    --data-file=- \
                    --project="$PROJECT_ID" \
                    --replication-policy="automatic" 2>&1 | grep -v "WARNING" || true
                echo "    ⚠️  $secret_name creado vacío (actualiza el valor después)"
            fi
        fi
    else
        # No existe secreto de producción, crear nuevo
        echo "    Ingresa el valor para $secret_name (o presiona Enter para crear vacío):"
        read -s secret_value
        if [ -n "$secret_value" ]; then
            echo -n "$secret_value" | gcloud secrets create "$secret_name" \
                --data-file=- \
                --project="$PROJECT_ID" \
                --replication-policy="automatic" 2>&1 | grep -v "WARNING" || true
            echo "    ✅ $secret_name creado"
        else
            # Crear secreto vacío
            echo "dummy" | gcloud secrets create "$secret_name" \
                --data-file=- \
                --project="$PROJECT_ID" \
                --replication-policy="automatic" 2>&1 | grep -v "WARNING" || true
            echo "    ⚠️  $secret_name creado vacío (actualiza el valor después)"
        fi
    fi
done

echo ""
echo "=== Resumen ==="
echo "Secretos de desarrollo creados con sufijo: -$ENV_SUFFIX"
echo ""
echo "Para actualizar un secreto:"
echo "  echo 'nuevo_valor' | gcloud secrets versions add SECRETO-dev --data-file=- --project=$PROJECT_ID"
echo ""
echo "Para listar secretos de desarrollo:"
echo "  gcloud secrets list --project=$PROJECT_ID --filter='name:.*-dev'"
echo ""

