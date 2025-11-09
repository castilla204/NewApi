# 🔄 Actualizar Deployment para usar Secret

## Opción 1: Actualizar solo la variable de entorno (RÁPIDO)

Si ya tienes el deployment desplegado, puedes actualizar solo la variable `RABBITMQ_PASSWORD`:

```bash
kubectl set env deployment/new-api RABBITMQ_PASSWORD- -n default
kubectl set env deployment/new-api RABBITMQ_PASSWORD- -n default
kubectl patch deployment new-api -n default -p '{"spec":{"template":{"spec":{"containers":[{"name":"new-api","env":[{"name":"RABBITMQ_PASSWORD","valueFrom":{"secretKeyRef":{"name":"newapi-secrets","key":"rabbitmq-password"}}}]}]}]}}}'
```

O más simple, edita el deployment:

```bash
kubectl edit deployment new-api -n default
```

Y cambia esta línea:
```yaml
- name: RABBITMQ_PASSWORD
  value: "Pedrohabo1//"  # ❌ ELIMINAR ESTA LÍNEA
```

Por esta:
```yaml
- name: RABBITMQ_PASSWORD
  valueFrom:
    secretKeyRef:
      name: newapi-secrets
      key: rabbitmq-password
```

---

## Opción 2: Copiar el archivo al servidor

Desde tu máquina local (Windows), copia el archivo al servidor:

```powershell
# Desde PowerShell en tu máquina local
scp k8s/deployment.yaml root@srv742161:~/deployment.yaml
```

Luego en el servidor:
```bash
kubectl apply -f ~/deployment.yaml
```

---

## Opción 3: Crear el archivo directamente en el servidor

Crea el archivo `deployment.yaml` en el servidor con el contenido actualizado.

