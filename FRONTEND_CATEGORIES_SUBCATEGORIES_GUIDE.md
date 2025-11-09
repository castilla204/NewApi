# Guía Frontend: Sistema de Categorías y Subcategorías

## 📋 Índice
1. [Concepto General](#concepto-general)
2. [DTOs Disponibles](#dtos-disponibles)
3. [Endpoints Disponibles](#endpoints-disponibles)
4. [Ejemplos de Uso](#ejemplos-de-uso)
5. [Casos de Uso Comunes](#casos-de-uso-comunes)

---

## 🎯 Concepto General

El sistema permite crear **categorías** y **subcategorías** de forma jerárquica:

- **Categoría Padre**: Categoría principal sin categoría padre (`ParentId = null`)
- **Subcategoría**: Categoría que pertenece a una categoría padre (`ParentId != null`)

**Estructura jerárquica:**
```
Categoría Padre (Ej: "Tecnología")
  └── Subcategoría (Ej: "Desarrollo Web")
  └── Subcategoría (Ej: "Desarrollo Móvil")
```

**Reglas importantes:**
- ✅ Solo se pueden crear subcategorías bajo categorías padre (no bajo otras subcategorías)
- ✅ Una categoría padre puede tener múltiples subcategorías
- ✅ Las categorías padre deben estar activas para poder crear subcategorías bajo ellas

---

## 📦 DTOs Disponibles

### 1. `CategoryDto` (DTO Base)
DTO básico para categorías. Usado en respuestas simples.

```typescript
interface CategoryDto {
  id: number;
  name: string;
  parentId: number | null;  // null si es categoría padre
  isActive: boolean;
  createdAt: string;  // ISO DateTime
  updatedAt: string;  // ISO DateTime
}
```

### 2. `CategoryWithDetailsDto` (NUEVO - DTO Extendido)
Extiende `CategoryDto` con información adicional sobre la jerarquía.

```typescript
interface CategoryWithDetailsDto extends CategoryDto {
  isParent: boolean;              // true si ParentId es null
  hasSubcategories: boolean;      // true si tiene subcategorías activas
  subcategoriesCount: number;     // número de subcategorías activas
}
```

**Ejemplo de respuesta:**
```json
{
  "id": 1,
  "name": "Tecnología",
  "parentId": null,
  "isActive": true,
  "createdAt": "2024-01-15T10:00:00Z",
  "updatedAt": "2024-01-15T10:00:00Z",
  "isParent": true,
  "hasSubcategories": true,
  "subcategoriesCount": 3
}
```

### 3. `ParentCategoryDto` (NUEVO - Solo Categorías Padre)
DTO específico para categorías padre, usado en el selector de categorías padre.

```typescript
interface ParentCategoryDto {
  id: number;
  name: string;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  subcategoriesCount: number;  // cantidad de subcategorías que tiene
}
```

**Ejemplo de respuesta:**
```json
{
  "id": 1,
  "name": "Tecnología",
  "isActive": true,
  "createdAt": "2024-01-15T10:00:00Z",
  "updatedAt": "2024-01-15T10:00:00Z",
  "subcategoriesCount": 3
}
```

### 4. `CreateCategoryDto` (Para crear categorías/subcategorías)
DTO para crear nuevas categorías o subcategorías.

```typescript
interface CreateCategoryDto {
  name: string;
  parentId?: number | null;  // null o undefined para categoría padre, número para subcategoría
  isActive?: boolean;        // por defecto: true
}
```

**Ejemplos:**
- Crear categoría padre: `{ name: "Tecnología" }` o `{ name: "Tecnología", parentId: null }`
- Crear subcategoría: `{ name: "Desarrollo Web", parentId: 1 }`

### 5. `UpdateCategoryDto`
DTO para actualizar categorías existentes.

```typescript
interface UpdateCategoryDto {
  name: string;
  parentId?: number | null;
  isActive: boolean;
}
```

---

## 🔌 Endpoints Disponibles

### 1. `GET /api/categories` (ACTUALIZADO)
Obtiene todas las categorías activas con información detallada.

**Autenticación:** No requerida

**Respuesta:** `200 OK`
```json
[
  {
    "id": 1,
    "name": "Tecnología",
    "parentId": null,
    "isActive": true,
    "createdAt": "2024-01-15T10:00:00Z",
    "updatedAt": "2024-01-15T10:00:00Z",
    "isParent": true,
    "hasSubcategories": true,
    "subcategoriesCount": 3
  },
  {
    "id": 2,
    "name": "Desarrollo Web",
    "parentId": 1,
    "isActive": true,
    "createdAt": "2024-01-15T10:00:00Z",
    "updatedAt": "2024-01-15T10:00:00Z",
    "isParent": false,
    "hasSubcategories": false,
    "subcategoriesCount": 0
  }
]
```

**Cambios respecto a la versión anterior:**
- ✅ Ahora devuelve `CategoryWithDetailsDto[]` en lugar de `CategoryDto[]`
- ✅ Incluye campos adicionales: `isParent`, `hasSubcategories`, `subcategoriesCount`

**Ejemplo de uso:**
```typescript
const response = await fetch('/api/categories');
const categories: CategoryWithDetailsDto[] = await response.json();

// Filtrar solo categorías padre
const parentCategories = categories.filter(c => c.isParent);

// Filtrar solo subcategorías
const subcategories = categories.filter(c => !c.isParent);
```

---

### 2. `GET /api/categories/parents` (NUEVO)
Obtiene solo las categorías padre (sin subcategorías) para usar en selectores.

**Autenticación:** No requerida

**⚠️ IMPORTANTE:** 
- La URL debe ser exactamente: `/api/categories/parents` (sin barra final)
- Debe usar el método HTTP **GET** (no POST, PUT, etc.)
- Si recibes un error 405 Method Not Allowed, verifica que estés usando GET

**Respuesta:** `200 OK`
```json
{
  "success": true,
  "data": [
    {
      "id": 1,
      "name": "Tecnología",
      "isActive": true,
      "createdAt": "2024-01-15T10:00:00Z",
      "updatedAt": "2024-01-15T10:00:00Z",
      "subcategoriesCount": 3
    },
    {
      "id": 2,
      "name": "Salud",
      "isActive": true,
      "createdAt": "2024-01-15T10:00:00Z",
      "updatedAt": "2024-01-15T10:00:00Z",
      "subcategoriesCount": 0
    }
  ],
  "count": 2,
  "message": "Parent categories retrieved successfully"
}
```

**Uso recomendado:** Para poblar un selector/dropdown al crear subcategorías.

**Ejemplo de uso:**
```typescript
// ✅ CORRECTO: Usar GET explícitamente
const response = await fetch('/api/categories/parents', {
  method: 'GET',  // Asegúrate de especificar GET
  headers: {
    'Content-Type': 'application/json'
  }
});

if (!response.ok) {
  throw new Error(`Error ${response.status}: ${response.statusText}`);
}

const result = await response.json();

if (result.success) {
  const parentCategories: ParentCategoryDto[] = result.data;
  // Usar en un selector
  setParentCategories(parentCategories);
} else {
  console.error('Error:', result.message);
}
```

**Solución de problemas:**
- Si recibes **405 Method Not Allowed**: Verifica que estés usando `method: 'GET'` o simplemente `fetch('/api/categories/parents')` (GET es el método por defecto)
- Si recibes **404 Not Found**: Verifica que la URL sea exactamente `/api/categories/parents` (sin barra final `/`)
- Si recibes **500 Internal Server Error**: Revisa los logs del servidor

---

### 3. `POST /api/categories` (MEJORADO)
Crea una nueva categoría o subcategoría.

**Autenticación:** Requerida (Rol: Admin)

**Request Body:**
```json
{
  "name": "Desarrollo Web",
  "parentId": 1,        // Opcional: null o omitir para categoría padre
  "isActive": true      // Opcional: por defecto true
}
```

**Respuesta exitosa:** `201 Created`
```json
{
  "success": true,
  "message": "subcategoría 'Desarrollo Web' creada exitosamente",
  "data": {
    "id": 5,
    "name": "Desarrollo Web",
    "parentId": 1,
    "isActive": true,
    "createdAt": "2024-01-15T10:00:00Z",
    "updatedAt": "2024-01-15T10:00:00Z"
  }
}
```

**Validaciones del backend:**
- ✅ El nombre es requerido
- ✅ No puede existir otra categoría con el mismo nombre (case-insensitive)
- ✅ Si se proporciona `parentId`, la categoría padre debe existir
- ✅ Si se proporciona `parentId`, debe ser una categoría padre (no una subcategoría)
- ✅ La categoría padre debe estar activa

**Errores posibles:**
- `400 Bad Request`: Nombre vacío, nombre duplicado, categoría padre inválida
- `401 Unauthorized`: No autenticado
- `403 Forbidden`: No es Admin
- `500 Internal Server Error`: Error de base de datos

**Ejemplo de uso:**
```typescript
// Crear categoría padre
const createParentCategory = async (name: string) => {
  const response = await fetch('/api/categories', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify({ name })
  });
  
  if (response.ok) {
    const result = await response.json();
    console.log(result.message); // "categoría 'Tecnología' creada exitosamente"
    return result.data;
  } else {
    const error = await response.json();
    throw new Error(error.message);
  }
};

// Crear subcategoría
const createSubcategory = async (name: string, parentId: number) => {
  const response = await fetch('/api/categories', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify({ name, parentId })
  });
  
  if (response.ok) {
    const result = await response.json();
    console.log(result.message); // "subcategoría 'Desarrollo Web' creada exitosamente"
    return result.data;
  } else {
    const error = await response.json();
    throw new Error(error.message);
  }
};
```

---

### 4. `PUT /api/categories/{id}` (Sin cambios)
Actualiza una categoría existente.

**Autenticación:** Requerida

**Request Body:**
```json
{
  "name": "Nuevo Nombre",
  "parentId": null,    // Puede cambiar la jerarquía
  "isActive": true
}
```

**Respuesta:** `200 OK` - Devuelve `CategoryDto`

---

### 5. `POST /api/categories/fix-sequence` (Admin)
Corrige la secuencia de IDs en la base de datos (solo para administradores).

**Autenticación:** Requerida (Rol: Admin)

---

## 💡 Ejemplos de Uso

### Ejemplo 1: Listar todas las categorías con estructura jerárquica

```typescript
const fetchCategoriesWithHierarchy = async () => {
  const response = await fetch('/api/categories');
  const categories: CategoryWithDetailsDto[] = await response.json();
  
  // Separar categorías padre y subcategorías
  const parents = categories.filter(c => c.isParent);
  const subcategories = categories.filter(c => !c.isParent);
  
  // Crear estructura jerárquica
  const hierarchy = parents.map(parent => ({
    ...parent,
    subcategories: subcategories.filter(sub => sub.parentId === parent.id)
  }));
  
  return hierarchy;
};
```

### Ejemplo 2: Formulario para crear subcategoría

```typescript
import { useState, useEffect } from 'react';

const CreateSubcategoryForm = () => {
  const [parentCategories, setParentCategories] = useState<ParentCategoryDto[]>([]);
  const [selectedParentId, setSelectedParentId] = useState<number | null>(null);
  const [name, setName] = useState('');
  const [loading, setLoading] = useState(false);

  // Cargar categorías padre al montar el componente
  useEffect(() => {
    const loadParentCategories = async () => {
      const response = await fetch('/api/categories/parents');
      const result = await response.json();
      
      if (result.success) {
        setParentCategories(result.data);
      }
    };
    
    loadParentCategories();
  }, []);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    
    if (!selectedParentId || !name.trim()) {
      alert('Por favor, completa todos los campos');
      return;
    }

    setLoading(true);
    try {
      const response = await fetch('/api/categories', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({
          name: name.trim(),
          parentId: selectedParentId
        })
      });

      const result = await response.json();
      
      if (response.ok) {
        alert(result.message);
        setName('');
        setSelectedParentId(null);
        // Recargar lista de categorías si es necesario
      } else {
        alert(result.message || 'Error al crear la subcategoría');
      }
    } catch (error) {
      alert('Error de conexión');
    } finally {
      setLoading(false);
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      <div>
        <label>Categoría Padre:</label>
        <select 
          value={selectedParentId || ''} 
          onChange={(e) => setSelectedParentId(Number(e.target.value))}
          required
        >
          <option value="">Selecciona una categoría padre</option>
          {parentCategories.map(parent => (
            <option key={parent.id} value={parent.id}>
              {parent.name} ({parent.subcategoriesCount} subcategorías)
            </option>
          ))}
        </select>
      </div>
      
      <div>
        <label>Nombre de la Subcategoría:</label>
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          required
        />
      </div>
      
      <button type="submit" disabled={loading}>
        {loading ? 'Creando...' : 'Crear Subcategoría'}
      </button>
    </form>
  );
};
```

### Ejemplo 3: Mostrar árbol de categorías

```typescript
const CategoryTree = () => {
  const [categories, setCategories] = useState<CategoryWithDetailsDto[]>([]);

  useEffect(() => {
    const loadCategories = async () => {
      const response = await fetch('/api/categories');
      const data = await response.json();
      setCategories(data);
    };
    
    loadCategories();
  }, []);

  const parents = categories.filter(c => c.isParent);
  const subcategories = categories.filter(c => !c.isParent);

  return (
    <div>
      {parents.map(parent => {
        const parentSubcategories = subcategories.filter(
          sub => sub.parentId === parent.id
        );
        
        return (
          <div key={parent.id} style={{ marginLeft: '0px' }}>
            <strong>{parent.name}</strong>
            {parent.hasSubcategories && (
              <span> ({parent.subcategoriesCount} subcategorías)</span>
            )}
            
            {parentSubcategories.length > 0 && (
              <div style={{ marginLeft: '20px' }}>
                {parentSubcategories.map(sub => (
                  <div key={sub.id}>
                    └── {sub.name}
                  </div>
                ))}
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
};
```

---

## 🎯 Casos de Uso Comunes

### Caso 1: Crear una categoría padre
```typescript
POST /api/categories
{
  "name": "Tecnología"
}
```

### Caso 2: Crear una subcategoría
```typescript
// 1. Primero obtener las categorías padre disponibles
GET /api/categories/parents

// 2. Luego crear la subcategoría seleccionando una categoría padre
POST /api/categories
{
  "name": "Desarrollo Web",
  "parentId": 1  // ID de la categoría "Tecnología"
}
```

### Caso 3: Listar solo categorías padre para un selector
```typescript
const response = await fetch('/api/categories/parents');
const result = await response.json();
const parentOptions = result.data.map(p => ({
  value: p.id,
  label: `${p.name} (${p.subcategoriesCount} subcategorías)`
}));
```

### Caso 4: Verificar si una categoría tiene subcategorías
```typescript
const categories = await fetch('/api/categories').then(r => r.json());
const category = categories.find(c => c.id === 1);

if (category.hasSubcategories) {
  console.log(`Tiene ${category.subcategoriesCount} subcategorías`);
}
```

### Caso 5: Filtrar categorías por tipo
```typescript
const categories = await fetch('/api/categories').then(r => r.json());

// Solo categorías padre
const parents = categories.filter(c => c.isParent);

// Solo subcategorías
const subs = categories.filter(c => !c.isParent);

// Categorías padre sin subcategorías
const parentsWithoutSubs = categories.filter(
  c => c.isParent && !c.hasSubcategories
);
```

---

## ⚠️ Notas Importantes

1. **Autenticación**: Los endpoints de creación y actualización requieren autenticación. El endpoint de creación requiere rol de Admin.

2. **Validaciones del Backend**: El backend valida automáticamente:
   - Que el nombre no esté vacío
   - Que no exista otra categoría con el mismo nombre
   - Que la categoría padre exista y sea válida (no una subcategoría)
   - Que la categoría padre esté activa

3. **Manejo de Errores**: Siempre verifica el código de respuesta HTTP y maneja los errores apropiadamente:
   ```typescript
   if (!response.ok) {
     const error = await response.json();
     throw new Error(error.message);
   }
   ```

4. **Actualización de DTOs**: Si estás usando TypeScript, actualiza tus interfaces para incluir los nuevos campos:
   - `CategoryWithDetailsDto` en lugar de `CategoryDto` para `GET /api/categories`
   - `ParentCategoryDto` para `GET /api/categories/parents`

5. **Migración de Código Existente**: Si ya tienes código que usa `GET /api/categories`, ahora recibirás campos adicionales (`isParent`, `hasSubcategories`, `subcategoriesCount`). Tu código existente seguirá funcionando, pero puedes aprovechar estos nuevos campos.

---

## 📝 Resumen de Cambios

### Endpoints Modificados:
- ✅ `GET /api/categories` - Ahora devuelve `CategoryWithDetailsDto[]` con información adicional

### Endpoints Nuevos:
- ✅ `GET /api/categories/parents` - Devuelve solo categorías padre para selectores

### DTOs Nuevos:
- ✅ `CategoryWithDetailsDto` - Extiende `CategoryDto` con información jerárquica
- ✅ `ParentCategoryDto` - DTO específico para categorías padre

### Mejoras en Validaciones:
- ✅ Validación de que la categoría padre no sea una subcategoría
- ✅ Validación de que la categoría padre esté activa
- ✅ Mensajes de respuesta más descriptivos

---

¿Tienes preguntas o necesitas más ejemplos? ¡Pregunta al equipo de backend!

