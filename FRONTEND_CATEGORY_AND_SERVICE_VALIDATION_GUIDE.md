# Guía Frontend: Validación de Categorías y Servicios

## 📋 Resumen de Cambios

Se han implementado dos funcionalidades importantes:

1. **Validación de Servicios Únicos por Combinación**: Un experto puede tener **múltiples servicios de la misma categoría**, pero solo **uno por combinación de categoría + tipo de servicio**. Por ejemplo, puede tener "Coches + Revisión presencial" y "Coches + Revisión online", pero no dos servicios "Coches + Revisión presencial".
2. **Creación de Categorías (Solo Administradores)**: Solo los administradores pueden crear nuevas categorías. Los expertos deben solicitar a un administrador que cree una nueva categoría si no existe en el sistema.

---

## 1. Validación: Un Servicio por Combinación de Categoría y Tipo de Servicio

### 🚫 Comportamiento

Un experto puede tener **múltiples servicios de la misma categoría**, pero **solo uno por combinación de categoría + tipo de servicio**.

**Ejemplos permitidos:**
- ✅ Servicio "Coches" + "Revisión presencial"
- ✅ Servicio "Coches" + "Revisión online" (misma categoría, diferente tipo)
- ✅ Servicio "Motos" + "Revisión presencial"

**Ejemplos NO permitidos:**
- ❌ Servicio "Coches" + "Revisión presencial" (duplicado)
- ❌ Servicio "Coches" + "Revisión presencial" (mismo tipo en misma categoría)

Cuando un experto intenta crear un servicio con la misma combinación de categoría + tipo de servicio que ya tiene activo, el backend devuelve un error específico.

### 📡 Endpoint Afectado

**`POST /api/SearchService`**

### ⚠️ Respuesta de Error

**Status Code:** `400 Bad Request`

**Body:**
```json
{
  "message": "Ya tienes un servicio activo en la categoría 'Coches' con el tipo de servicio 'Revisión presencial'. Solo puedes tener un servicio por combinación de categoría y tipo de servicio. Puedes actualizar tu servicio existente, crear uno con otro tipo de servicio en la misma categoría, o crear uno en otra categoría.",
  "existingServiceId": 140,
  "categoryName": "Coches",
  "serviceTypeName": "Revisión presencial"
}
```

### 💡 Manejo en Frontend

#### Opción 1: Validación Preventiva (Recomendada)

Antes de permitir crear un servicio, verificar si el experto ya tiene un servicio activo en la categoría seleccionada:

```typescript
// Ejemplo en TypeScript/React
const checkExistingService = async (
  expertProfileId: number, 
  categoryId: number, 
  serviceTypeId: number
) => {
  try {
    const response = await fetch(
      `/api/SearchService/expert/${expertProfileId}?categoryId=${categoryId}&serviceTypeId=${serviceTypeId}`,
      {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      }
    );
    
    const services = await response.json();
    const activeServiceWithSameCombo = services.find(
      (s: any) => 
        s.categoryId === categoryId && 
        s.serviceTypeId === serviceTypeId && 
        s.isActive
    );
    
    return activeServiceWithSameCombo;
  } catch (error) {
    console.error('Error checking existing service:', error);
    return null;
  }
};

// Uso en el formulario de creación
const handleServiceTypeChange = async (categoryId: number, serviceTypeId: number) => {
  const existingService = await checkExistingService(expertProfileId, categoryId, serviceTypeId);
  
  if (existingService) {
    setError({
      message: `Ya tienes un servicio activo en la categoría '${existingService.categoryName}' con el tipo '${existingService.serviceTypeName}'.`,
      existingServiceId: existingService.id,
      categoryName: existingService.categoryName,
      serviceTypeName: existingService.serviceTypeName,
      action: 'update' // Sugerir actualizar en lugar de crear
    });
    setCanCreate(false);
  } else {
    setError(null);
    setCanCreate(true);
  }
};
```

#### Opción 2: Manejo de Error Post-Creación

Si el usuario intenta crear el servicio y recibe el error:

```typescript
const createService = async (serviceData: CreateServiceDto) => {
  try {
    const response = await fetch('/api/SearchService', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(serviceData)
    });

    if (!response.ok) {
      const error = await response.json();
      
      // ✅ Manejar error de combinación categoría + tipo de servicio duplicada
      if (error.existingServiceId && error.categoryName && error.serviceTypeName) {
        // Mostrar diálogo con opciones
        const userChoice = await showDialog({
          title: 'Servicio ya existe',
          message: error.message,
          buttons: [
            { text: 'Actualizar servicio existente', action: 'update' },
            { text: 'Cambiar tipo de servicio', action: 'change-type' },
            { text: 'Elegir otra categoría', action: 'change-category' },
            { text: 'Cancelar', action: 'cancel' }
          ]
        });

        if (userChoice === 'update') {
          // Redirigir a edición del servicio existente
          navigate(`/expert/services/${error.existingServiceId}/edit`);
        } else if (userChoice === 'change-type') {
          // Volver al selector de tipo de servicio
          setStep('service-type-selection');
        } else if (userChoice === 'change-category') {
          // Volver al selector de categoría
          setStep('category-selection');
        }
        return;
      }
      
      // Otros errores
      throw new Error(error.message || 'Error al crear el servicio');
    }

    const result = await response.json();
    return result;
  } catch (error) {
    console.error('Error creating service:', error);
    throw error;
  }
};
```

### 🎨 UI/UX Sugerencias

1. **En el selector de categoría y tipo de servicio:**
   - Mostrar un indicador visual (🔒 o ⚠️) en combinaciones donde ya existe un servicio activo
   - Deshabilitar la opción o mostrar un tooltip: "Ya tienes un servicio con esta combinación de categoría y tipo"
   - Permitir seleccionar la misma categoría con un tipo de servicio diferente

2. **En el formulario de creación:**
   - Si detectas un servicio existente con la misma combinación, mostrar un banner:
     ```
     ⚠️ Ya tienes un servicio activo en "Coches" con tipo "Revisión presencial"
     [Actualizar servicio existente] [Cambiar tipo de servicio] [Cambiar categoría]
     ```

3. **Mensaje de error:**
   - Mostrar el mensaje completo del backend (incluye categoría y tipo de servicio)
   - Incluir botones de acción rápida:
     - "Ver servicio existente"
     - "Actualizar servicio"
     - "Cambiar tipo de servicio"
     - "Elegir otra categoría"

---

## 2. Creación de Categorías (Solo Administradores)

### ✨ Funcionalidad

**IMPORTANTE:** Solo los **administradores** pueden crear nuevas categorías. Los expertos deben solicitar a un administrador que cree una nueva categoría si no existe en el sistema.

### 📡 Endpoint

**`POST /api/Categories`**

**Autorización:** Requiere rol `Admin` (solo administradores)

### 📥 Request

**Headers:**
```
Authorization: Bearer {admin_token}
Content-Type: application/json
```

**Body (JSON):**
```typescript
interface CreateCategoryDto {
  name: string;           // ✅ REQUERIDO - Nombre de la categoría (no puede estar vacío)
  parentId?: number;     // ⚠️ OPCIONAL - ID de categoría padre (si es subcategoría)
  isActive?: boolean;     // ⚠️ OPCIONAL - Si la categoría está activa (por defecto: true)
}
```

**Ejemplo 1 - Categoría principal:**
```json
{
  "name": "Motos de Agua",
  "isActive": true
}
```

**Ejemplo 2 - Subcategoría (con parentId):**
```json
{
  "name": "Motos de Agua - Recreativas",
  "parentId": 15,
  "isActive": true
}
```

**Ejemplo 3 - Mínimo requerido (solo name):**
```json
{
  "name": "Motos de Agua"
}
```

**Validaciones:**
- ✅ `name` es **obligatorio** y no puede estar vacío o solo espacios
- ✅ `name` se trimea automáticamente (espacios al inicio/final se eliminan)
- ✅ No puede haber dos categorías con el mismo nombre (comparación case-insensitive)
- ⚠️ Si se proporciona `parentId`, debe existir una categoría con ese ID
- ⚠️ `isActive` por defecto es `true` si no se especifica

### 📤 Response

**Success (201 Created):**
```json
{
  "id": 15,
  "name": "Motos de Agua",
  "parentId": null,
  "isActive": true,
  "createdAt": "2025-11-05T12:00:00Z",
  "updatedAt": "2025-11-05T12:00:00Z"
}
```

**Error (400 Bad Request):**
```json
{
  "message": "Ya existe una categoría con el nombre 'Motos de Agua'. Por favor, elige otro nombre.",
  "existingCategoryId": 12,
  "existingCategoryName": "Motos de Agua"
}
```

**Error (400 Bad Request) - Nombre vacío:**
```json
{
  "message": "El nombre de la categoría es requerido"
}
```

**Error (400 Bad Request) - ParentId inválido:**
```json
{
  "message": "La categoría padre con ID 999 no existe"
}
```

### 💡 Implementación en Frontend

**⚠️ IMPORTANTE:** Este endpoint solo está disponible para usuarios con rol `Admin`. Los expertos no pueden crear categorías directamente.

#### Componente de Creación de Categoría (Solo Admin)

```typescript
interface CreateCategoryFormData {
  name: string;
  parentId?: number;
  isActive?: boolean;
}

const CreateCategoryForm: React.FC = () => {
  const [formData, setFormData] = useState<CreateCategoryFormData>({
    name: '',
    parentId: undefined
  });
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const response = await fetch('/api/Categories', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          name: formData.name.trim(),
          parentId: formData.parentId || null,
          isActive: true
        })
      });

      if (!response.ok) {
        const errorData = await response.json();
        
        // Manejar error de categoría duplicada
        if (errorData.existingCategoryId) {
          setError(
            `Ya existe una categoría con el nombre "${errorData.existingCategoryName}". ` +
            `¿Quieres usar la categoría existente?`
          );
          // Opcional: Mostrar botón para usar categoría existente
          setExistingCategoryId(errorData.existingCategoryId);
          return;
        }
        
        throw new Error(errorData.message || 'Error al crear la categoría');
      }

      const newCategory = await response.json();
      
      // ✅ Categoría creada exitosamente
      onCategoryCreated(newCategory);
      
      // Resetear formulario
      setFormData({ name: '', parentId: undefined });
      
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Error desconocido');
    } finally {
      setLoading(false);
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      <div>
        <label>Nombre de la categoría *</label>
        <input
          type="text"
          value={formData.name}
          onChange={(e) => setFormData({ ...formData, name: e.target.value })}
          placeholder="Ej: Motos de Agua"
          required
          minLength={1}
        />
      </div>

      {/* Opcional: Selector de categoría padre */}
      <div>
        <label>Categoría padre (opcional)</label>
        <select
          value={formData.parentId || ''}
          onChange={(e) => setFormData({ 
            ...formData, 
            parentId: e.target.value ? parseInt(e.target.value) : undefined 
          })}
        >
          <option value="">Ninguna (categoría principal)</option>
          {parentCategories.map(cat => (
            <option key={cat.id} value={cat.id}>{cat.name}</option>
          ))}
        </select>
      </div>

      {error && (
        <div className="error-message">
          {error}
        </div>
      )}

      <button type="submit" disabled={loading || !formData.name.trim()}>
        {loading ? 'Creando...' : 'Crear Categoría'}
      </button>
    </form>
  );
};
```

#### Para Expertos: Solicitar Nueva Categoría

Si un experto necesita una categoría que no existe, debe solicitar a un administrador que la cree. El frontend puede mostrar un mensaje o formulario de solicitud:

```typescript
const ServiceCreationFlow: React.FC = () => {
  const [step, setStep] = useState<'category' | 'service-details'>('category');
  const [selectedCategory, setSelectedCategory] = useState<Category | null>(null);
  const userRole = useUserRole(); // Hook para obtener el rol del usuario

  return (
    <div>
      {step === 'category' && (
        <div>
          <h2>Selecciona una categoría</h2>
          
          {/* Lista de categorías existentes */}
          <CategoryList
            categories={categories}
            onSelect={(category) => {
              setSelectedCategory(category);
              setStep('service-details');
            }}
          />

          {/* Solo Admin puede crear categorías */}
          {userRole === 'Admin' && (
            <button onClick={() => setShowCreateCategory(true)}>
              + Crear nueva categoría
            </button>
          )}

          {/* Para Expertos: Mostrar opción de solicitar categoría */}
          {userRole === 'Expert' && (
            <div className="request-category-section">
              <p>¿No encuentras tu categoría?</p>
              <button onClick={() => setShowRequestCategory(true)}>
                Solicitar nueva categoría
              </button>
              <p className="help-text">
                Un administrador revisará tu solicitud y creará la categoría si es apropiada.
              </p>
            </div>
          )}
        </div>
      )}
    </div>
  );
};
```

### 🎨 UI/UX Sugerencias

#### Para Administradores:

1. **Botón "Crear nueva categoría":**
   - Colocarlo cerca del selector de categorías
   - Icono: `+` o `➕`
   - Texto: "Crear nueva categoría" o "Añadir categoría"
   - Solo visible para usuarios con rol `Admin`

2. **Formulario de creación:**
   - Campo `name` obligatorio con validación en tiempo real
   - Campo `parentId` opcional con selector de categorías existentes
   - Checkbox `isActive` (marcado por defecto)

3. **Validación en tiempo real:**
   - Verificar duplicados mientras el usuario escribe
   - Mostrar sugerencias si hay categorías similares
   - Validar que `parentId` existe si se proporciona

4. **Feedback visual:**
   - Mostrar spinner durante la creación
   - Mensaje de éxito: "Categoría 'Motos de Agua' creada exitosamente"
   - Actualizar lista de categorías automáticamente

5. **Manejo de errores:**
   - Si la categoría ya existe, ofrecer usar la existente
   - Mostrar el ID y nombre de la categoría existente para referencia

#### Para Expertos:

1. **Opción "Solicitar nueva categoría":**
   - Mostrar un botón o enlace: "¿No encuentras tu categoría? Solicitar nueva"
   - Al hacer clic, mostrar un formulario de solicitud (no creación directa)
   - El formulario puede incluir:
     - Nombre propuesto de la categoría
     - Descripción/justificación
     - Categoría padre sugerida (opcional)
   - Enviar la solicitud a los administradores (email, notificación, etc.)

2. **Mensaje informativo:**
   - Explicar que un administrador revisará la solicitud
   - Indicar tiempo estimado de respuesta (si aplica)

---

## 📝 Resumen de Endpoints

### Crear Servicio
```
POST /api/SearchService
Authorization: Bearer {token}
Content-Type: multipart/form-data

Body:
- ExpertProfileId: number
- CategoryId: number
- ServiceTypeId: number
- Price: number
- Conditions: string
- DurationInHours: number
- Images: File[]
- SelectedDeliverableTypes: string (JSON array)

Error 400 (Combinación categoría + tipo duplicada):
{
  "message": "Ya tienes un servicio activo en la categoría 'X' con el tipo de servicio 'Y'...",
  "existingServiceId": number,
  "categoryName": string,
  "serviceTypeName": string
}
```

### Crear Categoría (Solo Admin)
```
POST /api/Categories
Authorization: Bearer {admin_token}
Content-Type: application/json

Body (JSON):
{
  "name": string,              // ✅ REQUERIDO - Nombre de la categoría
  "parentId": number?,         // ⚠️ OPCIONAL - ID de categoría padre
  "isActive": boolean?         // ⚠️ OPCIONAL - Activa/inactiva (default: true)
}

Ejemplo Request:
{
  "name": "Motos de Agua",
  "isActive": true
}

Success 201 Created:
{
  "id": 15,
  "name": "Motos de Agua",
  "parentId": null,
  "isActive": true,
  "createdAt": "2025-11-05T12:00:00Z",
  "updatedAt": "2025-11-05T12:00:00Z"
}

Error 400 Bad Request (Nombre vacío):
{
  "message": "El nombre de la categoría es requerido"
}

Error 400 Bad Request (Duplicado):
{
  "message": "Ya existe una categoría con el nombre 'Motos de Agua'. Por favor, elige otro nombre.",
  "existingCategoryId": 12,
  "existingCategoryName": "Motos de Agua"
}

Error 400 Bad Request (ParentId inválido):
{
  "message": "La categoría padre con ID 999 no existe"
}

Error 401 Unauthorized (No es Admin):
{
  "message": "No autorizado"
}

Error 403 Forbidden (Rol incorrecto):
{
  "message": "No tienes permisos para realizar esta acción"
}
```

---

## ✅ Checklist de Implementación Frontend

- [ ] Agregar validación preventiva antes de crear servicio (verificar combinación categoría + tipo duplicada)
- [ ] Manejar error 400 con `existingServiceId`, `categoryName` y `serviceTypeName`
- [ ] Mostrar UI para sugerir actualizar servicio existente, cambiar tipo de servicio o cambiar categoría
- [ ] **Solo Admin:** Implementar formulario de creación de categoría (verificar rol antes de mostrar)
- [ ] **Solo Admin:** Validar que el usuario tiene rol `Admin` antes de permitir crear categoría
- [ ] **Para Expertos:** Implementar formulario de solicitud de categoría (no creación directa)
- [ ] Manejar errores de categoría duplicada (ofrecer usar existente)
- [ ] Validar nombre de categoría (no vacío, trim)
- [ ] Validar `parentId` si se proporciona (debe existir)
- [ ] Mostrar feedback visual durante creación
- [ ] Actualizar lista de categorías después de crear una nueva
- [ ] Manejar errores 401/403 si un experto intenta crear categoría

---

## 🔗 Endpoints Relacionados

- `GET /api/Categories` - Obtener todas las categorías activas
- `GET /api/SearchService/expert/{expertProfileId}` - Obtener servicios de un experto
- `PUT /api/SearchService` - Actualizar servicio existente

