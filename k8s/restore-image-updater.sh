#!/bin/bash
# Script para restaurar configuración de Image Updater después de reinicio
# Este script aplica las anotaciones a las Applications de ArgoCD

set -e

echo "=== Restaurando configuración de Image Updater ==="

# Aplicar anotaciones a new-api
kubectl annotate application new-api -n argocd \
  argocd-image-updater.argoproj.io/image-list=new-api=erizo9/newapi \
  argocd-image-updater.argoproj.io/new-api.update-strategy=semver \
  argocd-image-updater.argoproj.io/new-api.allow-tags="regexp:^v?[0-9]+\\.[0-9]+\\.[0-9]+$" \
  argocd-image-updater.argoproj.io/write-back-method=git \
  argocd-image-updater.argoproj.io/git-branch=main \
  --overwrite 2>/dev/null || echo "new-api ya tiene anotaciones"

# Aplicar anotaciones a react-web
kubectl annotate application react-web -n argocd \
  argocd-image-updater.argoproj.io/image-list=react-web=erizo9/reactweb \
  argocd-image-updater.argoproj.io/react-web.update-strategy=semver \
  argocd-image-updater.argoproj.io/react-web.allow-tags="regexp:^v?[0-9]+\\.[0-9]+\\.[0-9]+$" \
  argocd-image-updater.argoproj.io/write-back-method=git \
  argocd-image-updater.argoproj.io/git-branch=main \
  --overwrite 2>/dev/null || echo "react-web ya tiene anotaciones"

echo "✅ Configuración de Image Updater restaurada"

