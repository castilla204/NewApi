# 🔐 Configuración de Secretos - newapi

## ⚠️ IMPORTANTE: Secretos Eliminados del Código

Se han eliminado todos los secretos hardcodeados del código. Ahora deben configurarse mediante:

1. **Variables de entorno** (desarrollo local)
2. **Google Cloud Secret Manager** (producción)
3. **Kubernetes Secrets** (cluster)

## 📋 Secretos que Necesitas Configurar

### Para Desarrollo Local:

#### Opción 1: User Secrets (Recomendado)
```bash
dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
dotnet user-secrets set "Stripe:GeneralWebhookSecret" "whsec_..."
dotnet user-secrets set "ConnectionStrings:PostgresConnection" "Host=..."
```

#### Opción 2: Variables de Entorno
```bash
export STRIPE_SECRET_KEY="sk_test_..."
export STRIPE_WEBHOOK_SECRET="whsec_..."
export STRIPE_GENERAL_WEBHOOK_SECRET="whsec_..."
export POSTGRES_CONNECTION="Host=..."
```

### Para Producción (Kubernetes):

Los secretos ya están configurados en Google Cloud Secret Manager y se cargan automáticamente en producción.

## 🔧 Secretos de Kubernetes

### regcred (Docker Hub)
```bash
kubectl create secret docker-registry regcred \
  --docker-server=docker.io \
  --docker-username=erizo9 \
  --docker-password=<TU_PASSWORD> \
  --docker-email=dcastilla@gmail.com \
  -n default
```

### newapi-secrets (RabbitMQ)
```bash
kubectl create secret generic newapi-secrets \
  --from-literal=rabbitmq-password=<PASSWORD> \
  -n default
```

### newapi-credentials (Google Cloud)
```bash
kubectl create secret generic newapi-credentials \
  --from-file=cloudcredential.json=<path-to-file> \
  -n default
```

## ✅ Verificación

Después de configurar, verifica que no hay secretos en el código:
```bash
# Trivy debería pasar sin detectar secretos
trivy fs . --scanners secret
```

