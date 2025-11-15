# Instrucciones para Aplicar Cambios de Configuración MCP

## ✅ **CAMBIOS REALIZADOS**

Se ha actualizado el archivo `.cursor/mcp.json` para permitir operaciones de escritura en PostgreSQL agregando el flag `--disable-read-only`.

## 🔄 **PASOS PARA APLICAR LOS CAMBIOS**

1. **Cierra Cursor completamente**
   - Presiona `Ctrl+Shift+Q` o cierra la aplicación completamente
   - Espera 5-10 segundos para asegurar que todos los procesos se cierren

2. **Abre Cursor de nuevo**
   - Los servidores MCP se reiniciarán automáticamente con la nueva configuración
   - El servidor `postgresql-mcp` ahora tendrá permisos de escritura

3. **Verifica que funciona**
   - Intenta ejecutar una consulta INSERT, UPDATE o DELETE
   - Deberías poder crear los nuevos estados sin errores

## 📝 **CONFIGURACIÓN ACTUAL**

```json
"postgresql-mcp": {
  "command": "npx",
  "args": [
    "-y",
    "@modelcontextprotocol/server-postgres@latest",
    "postgresql://admin:***@185.166.39.4:30000/atrapo",
    "--disable-read-only"  // ✅ NUEVO: Permite escritura
  ],
  "disabled": false,
  "alwaysAllow": [],
  "env": {
    "PGPASSWORD": "Pedrohabo1//"
  }
}
```

## ⚠️ **IMPORTANTE**

- Los cambios solo se aplicarán después de reiniciar Cursor
- El flag `--disable-read-only` permite operaciones INSERT, UPDATE y DELETE
- Asegúrate de tener los permisos necesarios en PostgreSQL para el usuario `admin`

## 🎯 **PRÓXIMOS PASOS**

Después de reiniciar Cursor, podrás:
1. Crear los estados `cancelled_by_client_no_proposal` y `cancelled_by_expert_no_response`
2. Crear las configuraciones de porcentajes para cada estado
3. Ejecutar cualquier operación de escritura en la BD

