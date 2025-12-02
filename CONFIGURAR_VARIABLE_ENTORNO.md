# 🔧 Configurar Variable de Entorno GOOGLE_APPLICATION_CREDENTIALS

## 📋 Situación Actual

Tienes la variable de entorno `GOOGLE_APPLICATION_CREDENTIALS` configurada apuntando a:
```
C:\cloudcredential.json
```

Y estás ejecutando `gcloud auth application-default login` que generará credenciales en:
```
C:\Users\Diego\AppData\Roaming\gcloud\application_default_credentials.json
```

## ✅ Opciones

### Opción 1: Usar las Credenciales Generadas por gcloud (Recomendado)

Si quieres usar las credenciales que genera `gcloud auth application-default login`:

1. **Continuar con "Y"** en el comando actual
2. **Desconfigurar la variable de entorno** después:

```powershell
# Desconfigurar la variable de entorno (solo para esta sesión)
$env:GOOGLE_APPLICATION_CREDENTIALS = $null

# O desconfigurarla permanentemente
[System.Environment]::SetEnvironmentVariable('GOOGLE_APPLICATION_CREDENTIALS', $null, 'User')
```

3. **Verificar que funciona**:
```bash
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

### Opción 2: Usar el Archivo JSON que Ya Tienes

Si prefieres usar el archivo `C:\cloudcredential.json` que ya tienes:

1. **Cancelar** el comando actual (presiona "n")
2. **Verificar que el archivo existe**:
```powershell
Test-Path C:\cloudcredential.json
```

3. **Si existe, ya está listo**. La aplicación lo usará automáticamente.

## 🎯 Recomendación

**Usa la Opción 1** (credenciales generadas por gcloud) porque:
- Es más fácil de mantener
- Se actualiza automáticamente
- No necesitas gestionar archivos manualmente

## 📝 Pasos Completos (Opción 1)

### Paso 1: Continuar con el Login

Presiona **"Y"** y sigue las instrucciones en el navegador.

### Paso 2: Desconfigurar Variable de Entorno

Después de que termine el login, ejecuta:

```powershell
# Verificar variable actual
$env:GOOGLE_APPLICATION_CREDENTIALS

# Desconfigurarla (solo esta sesión)
Remove-Item Env:\GOOGLE_APPLICATION_CREDENTIALS

# O desconfigurarla permanentemente
[System.Environment]::SetEnvironmentVariable('GOOGLE_APPLICATION_CREDENTIALS', $null, 'User')
```

### Paso 3: Verificar

```bash
# Esto debería funcionar ahora
gcloud secrets versions access latest --secret=jwt-key --project=grup-441318
```

### Paso 4: Ejecutar Aplicación

```bash
dotnet run
```

La aplicación usará automáticamente las credenciales generadas por gcloud.

## 🔍 Verificar Qué Credenciales se Están Usando

```powershell
# Verificar si la variable está configurada
$env:GOOGLE_APPLICATION_CREDENTIALS

# Si está vacía/null, usará las credenciales por defecto de gcloud
# Si tiene valor, usará ese archivo
```

## ⚠️ Nota Importante

- Si `GOOGLE_APPLICATION_CREDENTIALS` está configurada → usa ese archivo
- Si NO está configurada → usa las credenciales por defecto de gcloud

Para usar las credenciales generadas por gcloud, **debes desconfigurar la variable de entorno**.

