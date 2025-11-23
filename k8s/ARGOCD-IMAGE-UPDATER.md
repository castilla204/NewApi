# ArgoCD Image Updater - Configuración

## Estado
- ✅ Instalado (verificar con `kubectl get deployment -n argocd argocd-image-updater`)
- ⚠️  Configuración pendiente

## Configuración Requerida

### 1. Anotaciones en ArgoCD Applications

Para que Image Updater actualice automáticamente, agregar anotaciones a las Applications:

```yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  annotations:
    argocd-image-updater.argoproj.io/image-list: new-api=erizo9/newapi
    argocd-image-updater.argoproj.io/new-api.update-strategy: semver
    argocd-image-updater.argoproj.io/new-api.allow-tags: regexp:^v?[0-9]+\.[0-9]+\.[0-9]+$
    argocd-image-updater.argoproj.io/new-api.ignore-tags: latest,dev,test
    argocd-image-updater.argoproj.io/write-back-method: git
    argocd-image-updater.argoproj.io/git-branch: main
```

### 2. Configuración de Registries

Image Updater necesita acceso a los registries. Configurar en ConfigMap:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: argocd-image-updater-config
  namespace: argocd
data:
  registries.conf: |
    registries:
    - name: Docker Hub
      prefix: docker.io
      api_url: https://registry-1.docker.io
      credentials: ext:/scripts/dockerhub-token
    - name: GitHub Container Registry
      prefix: ghcr.io
      api_url: https://ghcr.io
      credentials: ext:/scripts/github-token
```

### 3. Permisos para Actualizar Git

Image Updater necesita permisos para escribir en los repositorios. Configurar secret:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: argocd-image-updater-secret
  namespace: argocd
type: Opaque
stringData:
  github-token: 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
```

## División de Trabajo con Renovate

- **Renovate**: Actualiza TODO en Git (90-95%)
  - Dependencias (npm, pip, gomod)
  - Dockerfiles
  - Manifests con versiones específicas
  
- **Image Updater**: Actualiza runtime (5-10%)
  - Imágenes con tag `latest`
  - Actualizaciones rápidas sin cambiar Git
  - Solo con anotaciones específicas

## Verificación

```bash
# Ver logs de Image Updater
kubectl logs -n argocd -l app.kubernetes.io/name=argocd-image-updater

# Ver actualizaciones
kubectl get imageupdaters -A

# Verificar anotaciones en Applications
kubectl get applications -n argocd -o yaml | grep argocd-image-updater
```

