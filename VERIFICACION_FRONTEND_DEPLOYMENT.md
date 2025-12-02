# ✅ Verificación del Deployment del Frontend

## 📋 Información de la Imagen

**Imagen:** `erizo9/reactweb:latest`  
**Digest SHA actual en Docker Hub:** `sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113`

**Incluye:**
- ✅ CSP configurado para permitir `api.atrapo.io`
- ✅ Cambios del PR #164 (Stripe, Elfsight widgets)

## 🔍 Comandos para Verificar

### 1. Verificar la imagen en el deployment:

```bash
kubectl get deployment react-web -n default -o yaml | grep image:
```

**Debería mostrar:**
```yaml
image: erizo9/reactweb@sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113
```

### 2. Verificar qué imagen están usando los pods:

```bash
kubectl get pods -n default -l app=react-web -o jsonpath='{.items[*].spec.containers[*].image}'
```

**Debería mostrar:**
```
erizo9/reactweb@sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113
```

### 3. Ver el estado del deployment:

```bash
kubectl get deployment react-web -n default
```

**Debería mostrar:**
- `READY`: 2/2 (o el número de réplicas configuradas)
- `UP-TO-DATE`: igual al número de réplicas
- `AVAILABLE`: igual al número de réplicas

### 4. Ver el rollout status:

```bash
kubectl rollout status deployment/react-web -n default
```

**Debería mostrar:**
```
deployment "react-web" successfully rolled out
```

### 5. Verificar en ArgoCD:

```bash
# Si tienes acceso a ArgoCD CLI
argocd app get react-web

# O verificar en la UI de ArgoCD:
# - Buscar la aplicación "react-web"
# - Verificar que el estado sea "Synced" y "Healthy"
# - Verificar que la imagen mostrada sea la correcta
```

### 6. Ver los eventos recientes:

```bash
kubectl get events -n default --sort-by='.lastTimestamp' | grep react-web | tail -10
```

## ✅ Estado Esperado

Si todo está correcto:

1. ✅ El deployment tiene la imagen con el digest SHA correcto
2. ✅ Los pods están usando la nueva imagen
3. ✅ El deployment está en estado "Ready"
4. ✅ ArgoCD muestra "Synced" y "Healthy"
5. ✅ No hay errores en los logs de los pods

## 🚨 Si hay problemas:

### Si los pods no se actualizan:

```bash
# Forzar el rollout
kubectl rollout restart deployment/react-web -n default

# Ver los logs
kubectl logs -l app=react-web -n default --tail=50
```

### Si ArgoCD no sincroniza:

```bash
# Sincronizar manualmente
argocd app sync react-web

# O desde la UI de ArgoCD, hacer click en "Sync"
```

---

**Última verificación:** 2025-01-XX  
**Digest SHA esperado:** `sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113`

