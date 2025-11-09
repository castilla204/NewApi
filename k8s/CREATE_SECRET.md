# 🔒 Crear Secret de Kubernetes de forma segura

## ✅ Opción 1: Crear Secret directamente (MÁS SEGURO)

Ejecuta este comando en tu terminal (reemplaza `Pedrohabo1//` con tu password real):

```bash
kubectl create secret generic newapi-secrets \
  --from-literal=rabbitmq-password='Pedrohabo1//' \
  --namespace=default
```

**Ventaja**: El password NO se guarda en ningún archivo, solo existe en Kubernetes.

---

## ⚠️ Opción 2: Usar el archivo YAML (menos seguro)

Si prefieres usar el archivo `newapi-secrets.yaml`:

```bash
kubectl apply -f k8s/newapi-secrets.yaml
```

**Nota**: El password está codificado en base64 en el archivo, pero sigue siendo visible.

---

## 🔍 Verificar que el Secret se creó correctamente

```bash
# Ver el Secret (sin mostrar el valor)
kubectl get secret newapi-secrets -n default

# Ver el valor decodificado (solo para verificación)
kubectl get secret newapi-secrets -n default -o jsonpath='{.data.rabbitmq-password}' | base64 -d
```

---

## 🗑️ Si necesitas eliminar y recrear el Secret

```bash
kubectl delete secret newapi-secrets -n default
kubectl create secret generic newapi-secrets \
  --from-literal=rabbitmq-password='Pedrohabo1//' \
  --namespace=default
```

---

## 📝 Recomendación

**Usa la Opción 1** (comando directo) para mayor seguridad. El archivo `newapi-secrets.yaml` puede servir como documentación, pero considera agregarlo al `.gitignore` si contiene información sensible.

