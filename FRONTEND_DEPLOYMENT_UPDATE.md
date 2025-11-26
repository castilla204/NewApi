# 🚀 Actualizar Deployment del Frontend (reactweb)

## 📋 Información de la Nueva Imagen

**Imagen:** `erizo9/reactweb:latest`  
**Nuevo Digest SHA:** `sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113`

**Incluye:**
- ✅ CSP configurado para permitir `api.atrapo.io`
- ✅ Cambios del PR #164

## 🔄 Pasos para Actualizar

### Opción 1: Si el deployment está en el repositorio reactweb

1. **Ir al repositorio reactweb:**
   ```bash
   cd ../reactweb  # o la ruta donde esté el repo del frontend
   ```

2. **Buscar el archivo de deployment:**
   ```bash
   find . -name "*deployment*.yaml" -o -name "*deployment*.yml"
   ```

3. **Actualizar la imagen en el deployment:**
   ```yaml
   image: erizo9/reactweb@sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113
   ```

4. **Commit y push:**
   ```bash
   git add k8s/deployment.yaml  # o el archivo correspondiente
   git commit -m "Actualizar imagen Docker del frontend con CSP configurado"
   git push
   ```

### Opción 2: Si el deployment está gestionado por ArgoCD

1. **Verificar en ArgoCD UI:**
   - Buscar la aplicación del frontend (probablemente `react-web` o `reactweb`)
   - Verificar que esté sincronizada con el repositorio

2. **Si ArgoCD está configurado para auto-sync:**
   - Solo necesitas actualizar el archivo en el repositorio reactweb
   - ArgoCD detectará el cambio y sincronizará automáticamente

### Opción 3: Actualizar directamente con kubectl

Si tienes acceso al cluster:

```bash
# Ver el deployment actual
kubectl get deployment react-web -n default -o yaml | grep image:

# Actualizar la imagen
kubectl set image deployment/react-web react-web=erizo9/reactweb@sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113 -n default

# Verificar el rollout
kubectl rollout status deployment/react-web -n default
```

## ✅ Verificación

Después de actualizar, verifica:

```bash
# Ver qué imagen están usando los pods
kubectl get pods -n default -l app=react-web -o jsonpath='{.items[*].spec.containers[*].image}'

# Ver el estado del deployment
kubectl get deployment react-web -n default
```

## 📝 Nota

El deployment del frontend está en el repositorio **reactweb**, no en este repositorio (newApi/backend).

---

**Última actualización:** 2025-01-XX  
**Nuevo digest SHA:** `sha256:4f29b1464e9a83d9c9b733d0067fa7dc1570c75109ff3e2d60ea66737fe38113`

