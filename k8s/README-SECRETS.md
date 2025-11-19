# Gestión de Secretos - Kubernetes

## ⚠️ IMPORTANTE: No commitees secretos en el repositorio

Los secretos de Kubernetes deben crearse directamente en el cluster, NO en archivos YAML en el repositorio.

## 🔐 Secretos Requeridos

### 1. regcred (Docker Hub credentials)

**Crear en el cluster:**
```bash
kubectl create secret docker-registry regcred \
  --docker-server=docker.io \
  --docker-username=erizo9 \
  --docker-password=<TU_PASSWORD> \
  --docker-email=dcastilla@gmail.com \
  -n default
```

**O desde un archivo:**
```bash
# Crear el archivo localmente (NO commitearlo)
kubectl create secret docker-registry regcred \
  --from-file=.dockerconfigjson=<path-to-docker-config> \
  --type=kubernetes.io/dockerconfigjson \
  -n default
```

### 2. newapi-secrets (RabbitMQ password, etc.)

**Crear en el cluster:**
```bash
kubectl create secret generic newapi-secrets \
  --from-literal=rabbitmq-password=<PASSWORD> \
  -n default
```

### 3. newapi-credentials (Google Cloud credentials)

**Crear en el cluster:**
```bash
kubectl create secret generic newapi-credentials \
  --from-file=cloudcredential.json=<path-to-credentials-file> \
  -n default
```

## 📋 Mejores Prácticas

1. ✅ **NUNCA** commitees archivos con secretos reales
2. ✅ Usa `kubectl create secret` directamente
3. ✅ O usa herramientas como Sealed Secrets, External Secrets Operator
4. ✅ Los secretos se gestionan fuera del repositorio

## 🔄 Si necesitas versionar la estructura (sin valores):

Puedes crear un archivo `regcred-secret.yaml.example` con valores de ejemplo:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: regcred
  namespace: default
type: kubernetes.io/dockerconfigjson
data:
  .dockerconfigjson: <BASE64_ENCODED_DOCKER_CONFIG>
```

Pero **NUNCA** con valores reales.

