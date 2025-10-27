# 🚀 Cursor Backend Log Monitor

MCP Server para monitorear logs de tu API C# directamente en Cursor IDE.

## 📦 Instalación

### 1. Instalar Dependencias
```bash
cd cursor-backend-logs
npm install
```

### 2. Configurar Cursor
Copia el contenido de `cursor-mcp-config.json` a tu archivo de configuración MCP:

**Ubicación:** `~/.cursor/mcp.json` (Windows: `%USERPROFILE%\.cursor\mcp.json`)

```json
{
  "mcpServers": {
    "backend-logs": {
      "command": "node",
      "args": ["C:\\Users\\Diego\\OneDrive - Educacyl\\Escritorio\\App\\newApi\\cursor-backend-logs\\mcp-server.js"],
      "env": {
        "LOG_FILE_PATH": "C:\\Users\\Diego\\OneDrive - Educacyl\\Escritorio\\App\\newApi\\logs\\app.log"
      }
    }
  }
}
```

### 3. Configurar tu API C#
Agrega Serilog a tu `Program.cs` (ver `Program.cs.logging`)

### 4. Crear Directorio de Logs
```bash
mkdir logs
```

## 🎯 Uso en Cursor

Una vez configurado, puedes usar estos comandos en el chat de Cursor:

### 📖 Leer Logs
- "Lee los logs del backend"
- "Muestra los últimos 100 logs"
- "Solo errores del backend"

### 🔍 Buscar
- "Busca 'error' en los logs"
- "Encuentra logs de 'UserController'"

### 📊 Estadísticas
- "Estadísticas de logs"
- "¿Cuántos errores hay?"

### ⚡ Monitoreo en Tiempo Real
- "Inicia monitoreo de logs"
- "Para el monitoreo"

## 🛠️ Herramientas Disponibles

- `read_logs` - Leer logs del archivo
- `start_monitoring` - Monitoreo en tiempo real
- `stop_monitoring` - Parar monitoreo
- `get_log_stats` - Estadísticas de logs
- `search_logs` - Buscar en logs

## 🚨 Niveles de Log

- 🚨 **CRITICAL** - Errores críticos
- ❌ **ERROR** - Errores
- ⚠️ **WARN** - Advertencias
- ℹ️ **INFO** - Información
- 🐛 **DEBUG** - Debug

## 🔧 Solución de Problemas

1. **Log file not found**: Verifica que la ruta del archivo sea correcta
2. **MCP server not found**: Reinicia Cursor después de configurar
3. **No logs appearing**: Verifica que tu API esté escribiendo logs al archivo
