# ArgoCD Image Updater - Setup y Configuración

## Estado Actual

✅ **Image Updater instalado y funcionando**
- Deployment: `argocd-image-updater` en namespace `argocd`
- Versión: v0.12.2
- Monitoreo: Cada 2 minutos

## Configuración Aplicada

### Applications con Image Updater

1. **new-api**
   - Imagen: `erizo9/newapi`
   - Estrategia: semver
   - Tags permitidos: versiones semánticas (v1.0.0, 1.0.0, etc.)

2. **react-web**
   - Imagen: `erizo9/reactweb`
   - Estrategia: semver
   - Tags permitidos: versiones semánticas

### Deployments de ArgoCD

Los siguientes deployments de ArgoCD tienen anotaciones para auto-actualización:
- `argocd-server`
- `argocd-repo-server`
- `argocd-dex-server`

## Cómo Aplicar las Anotaciones

Las anotaciones están documentadas en:
- `k8s/argocd-applications/new-api-annotations.yaml`
- `k8s/argocd-applications/react-web-annotations.yaml`

Para aplicarlas:

```bash
kubectl annotate application new-api -n argocd \
  argocd-image-updater.argoproj.io/image-list=new-api=erizo9/newapi \
  argocd-image-updater.argoproj.io/new-api.update-strategy=semver \
  argocd-image-updater.argoproj.io/new-api.allow-tags="regexp:^v?[0-9]+\\.[0-9]+\\.[0-9]+$" \
  argocd-image-updater.argoproj.io/write-back-method=git \
  argocd-image-updater.argoproj.io/git-branch=main \
  --overwrite
```

## Verificación

```bash
# Ver logs de Image Updater
kubectl logs -n argocd -l app.kubernetes.io/name=argocd-image-updater

# Ver Applications con anotaciones
kubectl get applications -n argocd -o json | \
  jq -r '.items[] | select(.metadata.annotations."argocd-image-updater.argoproj.io/image-list") | .metadata.name'
```

## Persistencia

- ✅ Image Updater instalado vía Helm (persistente)
- ✅ Anotaciones aplicadas a Applications (persistente en Kubernetes)
- ✅ Documentación en Git (este archivo)
- ✅ Manifests de ejemplo en Git

## Restauración después de reinicio

Todo se restaurará automáticamente:
1. Helm releases se restauran automáticamente
2. Applications de ArgoCD se restauran desde Git
3. Anotaciones se pueden re-aplicar desde los manifests

