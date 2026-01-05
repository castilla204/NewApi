# 🔧 Instalar .NET SDK 10.0

## 🚨 Problema
El .NET Core SDK no está instalado o no está en el PATH. El proyecto requiere .NET 10.0.

## ✅ Solución Rápida

### Opción 1: Instalador Visual (Recomendado)

1. **Descargar el instalador:**
   - Ve a: https://dotnet.microsoft.com/download/dotnet/10.0
   - Descarga el **.NET SDK 10.0** (no solo el Runtime)
   - Ejecuta el instalador `.exe`

2. **Durante la instalación:**
   - ✅ Marca "Add to PATH" si aparece la opción
   - ✅ Instala todas las características

3. **Reiniciar Cursor/VS Code** después de la instalación

### Opción 2: Instalación con PowerShell (Automática)

Ejecuta este comando en PowerShell **como Administrador**:

```powershell
# Descargar e instalar .NET SDK 10.0
$url = "https://download.visualstudio.microsoft.com/download/pr/your-download-url"
# O usar winget si está disponible:
winget install Microsoft.DotNet.SDK.10
```

### Opción 3: Usar Chocolatey (Si lo tienes instalado)

```powershell
choco install dotnet-10.0-sdk
```

## 🔍 Verificar Instalación

Después de instalar, abre una **nueva terminal** y ejecuta:

```powershell
dotnet --version
```

Debería mostrar: `10.0.x` o similar

## 🔄 Si ya está instalado pero no funciona

Si el SDK está instalado pero no se encuentra, agrega manualmente al PATH:

1. Busca "Variables de entorno" en Windows
2. Edita la variable `Path` del sistema
3. Agrega: `C:\Program Files\dotnet`
4. Reinicia Cursor/VS Code

## 📋 Verificar PATH actual

Ejecuta esto para ver si dotnet está en el PATH:

```powershell
$env:PATH -split ';' | Select-String "dotnet"
```

---

**Nota:** Después de instalar, **cierra y vuelve a abrir Cursor/VS Code** para que detecte el SDK.























