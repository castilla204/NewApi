# 🚀 Instalación del MCP Server para Backend Logs

## ✅ **PASOS COMPLETADOS:**

### 1. ✅ Proyecto Creado
- Directorio: `cursor-backend-logs/`
- Dependencias instaladas: `@modelcontextprotocol/sdk`

### 2. ✅ Servidor MCP Creado
- Archivo: `simple-log-monitor.js`
- Funciona con `dotnet run` directamente

### 3. ✅ Configuración Lista
- Archivo: `cursor-mcp-config.json`
- Ruta del proyecto configurada

### 4. ✅ Logging Configurado
- `Program.cs` actualizado con logging básico
- Directorio `logs/` creado

## 🎯 **PASOS PARA COMPLETAR:**

### **Paso 1: Configurar Cursor MCP**

1. **Crear archivo de configuración MCP:**
   ```
   Ubicación: %USERPROFILE%\.cursor\mcp.json
   ```

2. **Copiar contenido de `cursor-mcp-config.json`:**
   ```json
   {
     "mcpServers": {
       "backend-logs": {
         "command": "node",
         "args": ["C:\\Users\\Diego\\OneDrive - Educacyl\\Escritorio\\App\\newApi\\cursor-backend-logs\\simple-log-monitor.js"],
         "env": {
           "PROJECT_PATH": "C:\\Users\\Diego\\OneDrive - Educacyl\\Escritorio\\App\\newApi"
         }
       }
     }
   }
   ```

### **Paso 2: Reiniciar Cursor**
- Cerrar Cursor completamente
- Abrir Cursor de nuevo
- El MCP server debería aparecer en la lista

### **Paso 3: Probar el MCP**

En el chat de Cursor, escribe:
- "Inicia monitoreo del backend"
- "Estado del monitoreo"
- "Para el monitoreo"

## 🎯 **Funcionalidades Disponibles:**

### **📊 Herramientas MCP:**
- `start_monitoring` - Inicia monitoreo en tiempo real
- `stop_monitoring` - Para el monitoreo
- `get_status` - Estado del monitoreo

### **🔍 Filtros Disponibles:**
- `ERROR` - Solo errores
- `WARN` - Solo advertencias  
- `INFO` - Solo información
- `DEBUG` - Solo debug
- `ALL` - Todo (por defecto)

## 🚨 **Solución de Problemas:**

### **1. MCP no aparece:**
- Verificar que el archivo `mcp.json` esté en la ubicación correcta
- Reiniciar Cursor completamente

### **2. Error de conexión:**
- Verificar que Node.js esté instalado
- Verificar que la ruta del proyecto sea correcta

### **3. No se ven logs:**
- Ejecutar `dotnet run` manualmente para verificar que funciona
- Verificar que el logging esté configurado en `Program.cs`

## 🎉 **¡Listo para usar!**

Una vez configurado, podrás monitorear los logs de tu API C# directamente desde Cursor IDE.
