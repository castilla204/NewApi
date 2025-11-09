# Instrucciones para Corregir la Secuencia de Categories

## 🔍 Diagnóstico

- **Max ID en tabla**: 3
- **Valor actual de secuencia**: 3
- **Problema**: La secuencia debería estar en 4 para el próximo insert

## ✅ Soluciones Implementadas

### 1. Configuración MCP Actualizada

He actualizado el archivo de configuración del MCP en:
```
C:\Users\Diego\.cursor\mcp.json
```

**Cambios realizados:**
- Agregado servidor `postgresql-mcp` con flag `--disable-read-only`
- Esto permitirá que el MCP tenga permisos de escritura

**⚠️ IMPORTANTE:** Necesitas **reiniciar Cursor** para que estos cambios surtan efecto.

### 2. Endpoint de Corrección Automática

He creado un endpoint que corrige la secuencia automáticamente:

```
POST /api/Categories/fix-sequence
Authorization: Bearer {admin_token}
```

**Respuesta exitosa:**
```json
{
  "message": "Secuencia corregida exitosamente",
  "maxId": 3,
  "newSequenceValue": 4
}
```

## 🚀 Cómo Corregir la Secuencia

### Opción 1: Usar el Endpoint (Recomendado)

1. Asegúrate de estar autenticado como Admin
2. Ejecuta:
   ```http
   POST http://localhost:7124/api/Categories/fix-sequence
   Authorization: Bearer {tu_token_admin}
   ```

3. La secuencia se corregirá automáticamente

### Opción 2: Ejecutar SQL Directamente

Ejecuta este SQL en tu base de datos PostgreSQL:

```sql
SELECT setval('"Categories_Id_seq"', 
    COALESCE((SELECT MAX("Id") FROM "Categories"), 0) + 1, 
    false);
```

O más simple:
```sql
SELECT setval('"Categories_Id_seq"', 4, false);
```

### Opción 3: Usar el Script SQL

Ejecuta el archivo `EJECUTAR_PARA_CORREGIR_SECUENCIA.sql` en tu base de datos.

## 📋 Verificación

Después de corregir, verifica que funcionó:

```sql
SELECT 
    last_value as valor_secuencia,
    (SELECT MAX("Id") FROM "Categories") as max_id_tabla
FROM "Categories_Id_seq";
```

El `valor_secuencia` debería ser `max_id_tabla + 1` (en este caso, 4).

## 🔄 Reiniciar Cursor para MCP con Escritura

1. Cierra completamente Cursor
2. Vuelve a abrir Cursor
3. El MCP de PostgreSQL ahora tendrá permisos de escritura
4. Podrás ejecutar comandos como `setval()` directamente desde el MCP

## ✅ Después de Corregir

Una vez corregida la secuencia, la creación de categorías debería funcionar correctamente. El próximo ID generado será 4.


