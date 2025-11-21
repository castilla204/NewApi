# Configuración de SignalR con Redis Backplane

## ¿Por qué necesitas Redis?

Con múltiples instancias de tu API (tienes `replicas: 2` y HPA hasta 10), **sin Redis**, los mensajes de SignalR NO se compartirán entre instancias:

- ❌ Usuario A conectado a Pod 1 → Envía mensaje → Solo lo ve Pod 1
- ❌ Usuario B conectado a Pod 2 → NO recibe el mensaje de Usuario A

**Con Redis**, todos los pods comparten los mensajes:
- ✅ Usuario A (Pod 1) envía mensaje → Redis → Todos los pods lo reciben
- ✅ Usuario B (Pod 2) recibe el mensaje correctamente

## Configuración en Kubernetes

### 1. Instalar Redis en tu cluster

```bash
# Opción 1: Usar Helm (recomendado)
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install redis bitnami/redis \
  --namespace default \
  --set auth.enabled=true \
  --set auth.password=TU_PASSWORD_AQUI \
  --set persistence.enabled=true

# Opción 2: Usar manifest YAML
kubectl apply -f redis-deployment.yaml
```

### 2. Crear Secret para Redis

```bash
# Crear el secret con la conexión a Redis
kubectl create secret generic redis-secret \
  --from-literal=connection-string="redis-svc.default.svc.cluster.local:6379,password=TU_PASSWORD_AQUI,abortConnect=false"
```

### 3. Agregar variable de entorno al Deployment

Edita `k8s/deployment.yaml` y agrega en la sección `env`:

```yaml
env:
  # ... otras variables ...
  
  # Redis para SignalR Backplane
  - name: REDIS_CONNECTION_STRING
    valueFrom:
      secretKeyRef:
        name: redis-secret
        key: connection-string
```

**O** agrega el secret a `newapi-secrets` si ya lo usas:

```yaml
- name: REDIS_CONNECTION_STRING
  valueFrom:
    secretKeyRef:
      name: newapi-secrets
      key: redis-connection-string
```

### 4. Agregar el secret a Google Cloud Secret Manager (opcional)

Si prefieres usar Secret Manager en lugar de Kubernetes secrets:

```bash
# Formato: host:port,password=xxx,abortConnect=false
gcloud secrets create redis-connection-string \
  --data-file=- <<< "redis-svc.default.svc.cluster.local:6379,password=TU_PASSWORD,abortConnect=false"
```

## Formato de Connection String

El formato correcto para Redis es:

```
host:port,password=xxx,abortConnect=false
```

Ejemplos:
- **Kubernetes interno**: `redis-svc.default.svc.cluster.local:6379,password=mipassword,abortConnect=false`
- **Localhost (desarrollo)**: `localhost:6379,abortConnect=false` (sin password)
- **Con autenticación**: `redis.example.com:6379,password=secret123,abortConnect=false`

## Verificación

Una vez configurado, verás en los logs de la aplicación:

```
✅ Redis backplane configurado para SignalR
```

Si no está configurado, verás:

```
⚠️ Redis no configurado para SignalR. Los mensajes NO se compartirán entre instancias.
```

## Desarrollo Local

En desarrollo, Redis **NO es necesario** porque solo hay 1 instancia. El código detecta automáticamente el entorno y solo configura Redis en producción.

## Troubleshooting

### Error: "Unable to connect to Redis"
- Verifica que Redis esté corriendo: `kubectl get pods -l app=redis`
- Verifica la connection string: `kubectl get secret redis-secret -o yaml`
- Verifica la red: `kubectl exec -it <pod-name> -- ping redis-svc`

### Los mensajes no se comparten entre pods
- Verifica que Redis esté configurado: Revisa los logs de la aplicación
- Verifica que todos los pods tengan la variable `REDIS_CONNECTION_STRING`
- Verifica la conectividad: `kubectl exec -it <pod-name> -- redis-cli -h redis-svc ping`

## Alternativas

Si no quieres usar Redis, puedes:
1. **Usar 1 sola réplica** (no escalable)
2. **Usar Azure SignalR Service** (si estás en Azure)
3. **Usar otro backplane** (RabbitMQ, etc.)

Pero **Redis es la solución más común y recomendada** para SignalR en Kubernetes.

