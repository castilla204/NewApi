# Configuración Segura de MCP para PostgreSQL

## ⚠️ IMPORTANTE: Seguridad

Este archivo `mcp.json` está configurado para usar variables de entorno. **NUNCA** hardcodees contraseñas aquí.

## Configuración

1. **Copia el archivo de ejemplo:**
   ```bash
   cp .cursor/.env.example .cursor/.env
   ```

2. **Edita `.cursor/.env` y agrega tu contraseña:**
   ```bash
   POSTGRES_PASSWORD=tu_contraseña_aqui
   ```

3. **Asegúrate de que `.env` está en `.gitignore`** (ya está configurado)

## Variables de Entorno

El archivo `mcp.json` usa estas variables:
- `POSTGRES_HOST` (default: 185.166.39.4)
- `POSTGRES_PORT` (default: 30000)
- `POSTGRES_DATABASE` (default: atrapo)
- `POSTGRES_USER` (default: admin)
- `POSTGRES_PASSWORD` (requerido) o `PGPASSWORD` (alternativa)

## Nota

Cursor debería cargar automáticamente las variables de entorno del sistema. Si no funciona, puedes:
- Configurar las variables en tu sistema operativo
- Usar un archivo `.env` que Cursor pueda leer (depende de la configuración)
