# 🔧 Solución de Errores en CI/CD del Frontend (ReactWeb)

## 🚨 Errores Detectados

### Error 1: ArgoCD CLI no instalado
```
argocd: command not found
```

### Error 2: Validación de Kubernetes falla
```
error validating "k8s/argocd-application.yaml": error validating data: failed to download openapi: Get "http://localhost:8080/openapi/v2?timeout=32s": dial tcp [::1]:8080: connect: connection refused
```

## ✅ Soluciones

### Solución 1: Instalar ArgoCD CLI en el workflow

En el archivo `.github/workflows/*.yml` del repositorio **ReactWeb**, antes de ejecutar `argocd app sync`, agrega:

```yaml
- name: Install ArgoCD CLI
  run: |
    curl -sSL -o argocd-linux-amd64 https://github.com/argoproj/argo-cd/releases/latest/download/argocd-linux-amd64
    sudo install -m 555 argocd-linux-amd64 /usr/local/bin/argocd
    rm argocd-linux-amd64

- name: Sync ArgoCD application
  run: |
    argocd app sync react-web --server ${{ secrets.ARGOCD_SERVER }} --auth-token ${{ secrets.ARGOCD_AUTH_TOKEN }}
  env:
    ARGOCD_SERVER: ${{ secrets.ARGOCD_SERVER }}
    ARGOCD_AUTH_TOKEN: ${{ secrets.ARGOCD_AUTH_TOKEN }}
```

### Solución 2: Deshabilitar validación de Kubernetes (si no es necesaria)

Si no necesitas validar los YAMLs en el CI/CD (ArgoCD ya lo hace), puedes deshabilitar la validación:

```yaml
- name: Apply Kubernetes manifests
  run: |
    kubectl apply --dry-run=client --validate=false -f k8s/
```

O usar `kubectl` con `--validate=false`:

```yaml
- name: Validate Kubernetes manifests
  run: |
    kubectl apply --dry-run=client --validate=false -f k8s/argocd-application.yaml
    kubectl apply --dry-run=client --validate=false -f k8s/deployment.yaml
    kubectl apply --dry-run=client --validate=false -f k8s/ingress.yaml
    kubectl apply --dry-run=client --validate=false -f k8s/network-policy.yaml
    kubectl apply --dry-run=client --validate=false -f k8s/service.yaml
```

### Solución 3: Usar acción de GitHub para ArgoCD (Recomendado)

En lugar de instalar ArgoCD CLI manualmente, usa una acción de GitHub:

```yaml
- name: Sync ArgoCD application
  uses: argoproj/argocd-actions@v1
  with:
    argocd-server: ${{ secrets.ARGOCD_SERVER }}
    argocd-username: ${{ secrets.ARGOCD_USERNAME }}
    argocd-password: ${{ secrets.ARGOCD_PASSWORD }}
    argocd-app-name: react-web
```

## 📋 Workflow Completo Recomendado

```yaml
name: Deploy Frontend

on:
  push:
    branches:
      - main

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout code
        uses: actions/checkout@v6

      - name: Install ArgoCD CLI
        run: |
          curl -sSL -o argocd-linux-amd64 https://github.com/argoproj/argo-cd/releases/latest/download/argocd-linux-amd64
          sudo install -m 555 argocd-linux-amd64 /usr/local/bin/argocd
          rm argocd-linux-amd64

      - name: Validate Kubernetes manifests (sin servidor)
        run: |
          # Validación básica de sintaxis YAML
          for file in k8s/*.yaml; do
            echo "Validating $file"
            python3 -c "import yaml; yaml.safe_load(open('$file'))" || exit 1
          done

      - name: Sync ArgoCD application
        run: |
          argocd app sync react-web \
            --server ${{ secrets.ARGOCD_SERVER }} \
            --auth-token ${{ secrets.ARGOCD_AUTH_TOKEN }} \
            --timeout 300
        continue-on-error: true  # No fallar si ArgoCD tiene auto-sync
```

## 🔑 Secrets Necesarios en GitHub

Asegúrate de tener estos secrets configurados en el repositorio **ReactWeb**:

- `ARGOCD_SERVER`: URL del servidor ArgoCD (ej: `https://argocd.example.com`)
- `ARGOCD_AUTH_TOKEN`: Token de autenticación de ArgoCD
- O alternativamente:
  - `ARGOCD_USERNAME`: Usuario de ArgoCD
  - `ARGOCD_PASSWORD`: Contraseña de ArgoCD

## ⚠️ Nota Importante

Si ArgoCD tiene **auto-sync** habilitado (como en el backend), **NO es necesario** sincronizar manualmente desde el CI/CD. ArgoCD detectará los cambios automáticamente.

En ese caso, puedes **eliminar el paso de sync** del workflow y solo validar los YAMLs:

```yaml
- name: Validate YAML syntax
  run: |
    for file in k8s/*.yaml; do
      python3 -c "import yaml; yaml.safe_load(open('$file'))" || exit 1
    done
```

## ✅ Verificación

Después de aplicar los cambios:

1. El workflow debería ejecutarse sin errores
2. ArgoCD debería sincronizar automáticamente (si tiene auto-sync)
3. Los pods deberían actualizarse con la nueva imagen

---

**Aplicar en:** Repositorio `ReactWeb` (frontend)  
**Archivo a modificar:** `.github/workflows/*.yml`

