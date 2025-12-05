# 📋 Instrucciones: Migración de Países (Country y ExpertCountry)

## ✅ Estado Actual

- ✅ **Modelos C#**: 
  - `ExpertProfile.Country` (string nullable) - País actual del experto
  - `SearchHire.ExpertCountry` (string nullable) - País del experto al momento de contratar
- ✅ **Migración creada**: `20250114120000_AddCountryToExpertProfileAndSearchHire.cs`
- ⏳ **Base de datos**: Pendiente de aplicar

## 📝 Cambios en la Base de Datos

La migración agregará dos columnas nuevas:

1. **`ExpertProfiles.Country`** (text, nullable)
   - Código de país ISO 3166-1 alpha-2 (ej: "ES", "US", "MX")
   - Se detecta automáticamente desde las coordenadas

2. **`SearchHires.ExpertCountry`** (text, nullable)
   - Snapshot del país del experto al momento de crear la contratación
   - No cambia aunque el experto se mude después

## 🚀 Pasos para Aplicar la Migración

### Opción 1: Usando dotnet ef (Recomendado)

```powershell
# 1. Navegar al directorio del proyecto
cd "C:\Users\Diego\Downloads\App\App\NewApi"

# 2. Aplicar la migración
dotnet ef database update

# O aplicar solo esta migración específica:
dotnet ef database update AddCountryToExpertProfileAndSearchHire
```

### Opción 2: Verificar migraciones pendientes primero

```powershell
# Ver lista de migraciones
dotnet ef migrations list

# Aplicar todas las pendientes
dotnet ef database update
```

### Opción 3: Aplicar manualmente en PostgreSQL (si es necesario)

Si por alguna razón no puedes usar `dotnet ef`, puedes ejecutar el SQL manualmente:

```sql
-- Agregar campo Country a ExpertProfiles
ALTER TABLE "ExpertProfiles"
ADD COLUMN "Country" text NULL;

-- Agregar campo ExpertCountry a SearchHires
ALTER TABLE "SearchHires"
ADD COLUMN "ExpertCountry" text NULL;
```

## 🔍 Verificación Post-Migración

### 1. Verificar en PostgreSQL

```sql
-- Verificar que las columnas existen
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable
FROM information_schema.columns
WHERE (table_name = 'ExpertProfiles' AND column_name = 'Country')
   OR (table_name = 'SearchHires' AND column_name = 'ExpertCountry');
```

**Resultado esperado:**
- `ExpertProfiles.Country`: `text`, `YES` (nullable)
- `SearchHires.ExpertCountry`: `text`, `YES` (nullable)

### 2. Verificar con EF Core

```powershell
dotnet ef migrations list
```

Debe mostrar que `AddCountryToExpertProfileAndSearchHire` está aplicada.

### 3. Probar la funcionalidad

Una vez aplicada la migración, prueba:

1. **Registrar un nuevo experto** con coordenadas:
   - El sistema debería detectar automáticamente el país
   - Verificar que `ExpertProfile.Country` se guarda correctamente

2. **Actualizar ubicación de un experto**:
   - Cambiar las coordenadas del experto
   - Verificar que `ExpertProfile.Country` se actualiza

3. **Crear una contratación**:
   - Verificar que `SearchHire.ExpertCountry` se guarda con el país del experto

## 📊 Datos Existentes

**Nota importante:** Los campos son **nullable**, por lo que:
- Los expertos existentes tendrán `Country = null` hasta que actualicen su ubicación
- Las contrataciones existentes tendrán `ExpertCountry = null`
- Esto es normal y esperado

Si quieres poblar los países de expertos existentes, puedes ejecutar un script que:
1. Obtenga las coordenadas de cada `ExpertProfile`
2. Llame a `TimezoneService.GetCountryFromCoordinatesAsync`
3. Actualice el campo `Country`

## ⚠️ Rollback (Si es Necesario)

Si necesitas revertir la migración:

```powershell
# Revertir a la migración anterior
dotnet ef database update <nombre_migracion_anterior>

# O eliminar las columnas manualmente en PostgreSQL:
```

```sql
ALTER TABLE "SearchHires" DROP COLUMN "ExpertCountry";
ALTER TABLE "ExpertProfiles" DROP COLUMN "Country";
```

## ✅ Checklist

- [ ] Migración creada: `20250114120000_AddCountryToExpertProfileAndSearchHire.cs`
- [ ] Aplicar migración con `dotnet ef database update`
- [ ] Verificar que las columnas existen en PostgreSQL
- [ ] Probar registro de nuevo experto (debe detectar país)
- [ ] Probar actualización de ubicación (debe actualizar país)
- [ ] Probar creación de contratación (debe guardar ExpertCountry)
- [ ] Verificar que los DTOs devuelven los campos correctamente

---

**¡Listo!** Una vez aplicada la migración, el sistema comenzará a detectar y guardar los países automáticamente. 🚀


