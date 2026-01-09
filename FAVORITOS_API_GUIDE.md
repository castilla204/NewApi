# 🔖 Guía de API - Sistema de Favoritos

## 📋 Descripción General

Sistema completo para que los usuarios puedan marcar servicios como favoritos, gestionar su lista de favoritos y ver una pestaña dedicada con sus servicios guardados.

---

## 🎯 Endpoints Disponibles

### Base URL
```
https://newapi-yn9v.onrender.com/api/Favorites
```

---

## 🔐 Autenticación

**TODOS los endpoints requieren autenticación** (excepto el contador público de favoritos).

Incluir el token JWT en el header:
```javascript
headers: {
    'Authorization': 'Bearer YOUR_JWT_TOKEN',
    'Content-Type': 'application/json'
}
```

---

## 📡 Endpoints

### 1. Toggle Favorito (Recomendado) ⭐

**Endpoint:** `POST /api/Favorites/toggle`

**Descripción:** Agrega o elimina un favorito con un solo click. Si el servicio ya es favorito, lo elimina. Si no lo es, lo agrega.

**Request Body:**
```json
{
    "searchServiceId": 123
}
```

**Response Success:**
```json
{
    "success": true,
    "message": "Servicio agregado a favoritos",
    "isFavorite": true,
    "searchServiceId": 123
}
```

**Ejemplo JavaScript:**
```javascript
async function toggleFavorite(serviceId) {
    try {
        const response = await fetch('https://newapi-yn9v.onrender.com/api/Favorites/toggle', {
            method: 'POST',
            headers: {
                'Authorization': `Bearer ${localStorage.getItem('authToken')}`,
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ searchServiceId: serviceId })
        });

        const data = await response.json();
        
        if (data.success) {
            console.log(data.isFavorite ? '❤️ Agregado a favoritos' : '🤍 Eliminado de favoritos');
            return data.isFavorite;
        }
    } catch (error) {
        console.error('Error:', error);
    }
}
```

---

### 2. Obtener Lista de Favoritos 📋

**Endpoint:** `GET /api/Favorites?page=1&pageSize=20`

**Descripción:** Obtiene todos los servicios favoritos del usuario autenticado con paginación.

**Query Parameters:**
- `page` (opcional): Número de página (default: 1)
- `pageSize` (opcional): Servicios por página (default: 20, max: 50)

**Response Success:**
```json
{
    "success": true,
    "data": [
        {
            "id": 1,
            "createdAt": "2026-01-09T20:00:00Z",
            "service": {
                "id": 123,
                "categoryId": 1,
                "categoryName": "Coches",
                "serviceTypeId": 5,
                "serviceTypeName": "Revisión ITV",
                "price": 50.00,
                "images": [
                    "https://storage.googleapis.com/image1.jpg",
                    "https://storage.googleapis.com/image2.jpg"
                ],
                "expertId": 10,
                "expertName": "Juan Pérez",
                "expertProfilePictureUrl": "https://storage.googleapis.com/profile.jpg",
                "expertCountry": "ES",
                "averageRating": 4.8,
                "completedSearches": 45,
                "isAvailableNow": false
            }
        }
    ],
    "pagination": {
        "currentPage": 1,
        "pageSize": 20,
        "totalCount": 5,
        "totalPages": 1
    }
}
```

**Ejemplo JavaScript:**
```javascript
async function getFavorites(page = 1) {
    try {
        const response = await fetch(
            `https://newapi-yn9v.onrender.com/api/Favorites?page=${page}&pageSize=20`,
            {
                headers: {
                    'Authorization': `Bearer ${localStorage.getItem('authToken')}`
                }
            }
        );

        const data = await response.json();
        
        if (data.success) {
            return data.data; // Array de favoritos con detalles del servicio
        }
    } catch (error) {
        console.error('Error:', error);
    }
}
```

---

### 3. Verificar si un Servicio es Favorito ✅

**Endpoint:** `GET /api/Favorites/check/{searchServiceId}`

**Descripción:** Verifica si un servicio específico es favorito del usuario.

**Response Success:**
```json
{
    "success": true,
    "data": {
        "isFavorite": true,
        "favoriteId": 1
    }
}
```

**Ejemplo JavaScript:**
```javascript
async function isFavorite(serviceId) {
    try {
        const response = await fetch(
            `https://newapi-yn9v.onrender.com/api/Favorites/check/${serviceId}`,
            {
                headers: {
                    'Authorization': `Bearer ${localStorage.getItem('authToken')}`
                }
            }
        );

        const data = await response.json();
        return data.data.isFavorite;
    } catch (error) {
        console.error('Error:', error);
        return false;
    }
}
```

---

### 4. Verificar Múltiples Servicios (Para Listas) 🔍

**Endpoint:** `POST /api/Favorites/check-multiple`

**Descripción:** Verifica múltiples servicios de una vez (útil para listas de servicios).

**Request Body:**
```json
[123, 456, 789]
```

**Response Success:**
```json
{
    "success": true,
    "data": {
        "123": true,
        "456": false,
        "789": true
    }
}
```

**Ejemplo JavaScript:**
```javascript
async function checkMultipleFavorites(serviceIds) {
    try {
        const response = await fetch(
            'https://newapi-yn9v.onrender.com/api/Favorites/check-multiple',
            {
                method: 'POST',
                headers: {
                    'Authorization': `Bearer ${localStorage.getItem('authToken')}`,
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(serviceIds)
            }
        );

        const data = await response.json();
        return data.data; // { "123": true, "456": false, ... }
    } catch (error) {
        console.error('Error:', error);
    }
}
```

---

### 5. Agregar a Favoritos (Método Directo) ➕

**Endpoint:** `POST /api/Favorites`

**Descripción:** Agrega un servicio a favoritos (usa `toggle` en su lugar para mejor UX).

**Request Body:**
```json
{
    "searchServiceId": 123
}
```

---

### 6. Eliminar de Favoritos (Método Directo) ➖

**Endpoint:** `DELETE /api/Favorites/{searchServiceId}`

**Descripción:** Elimina un servicio de favoritos (usa `toggle` en su lugar para mejor UX).

---

### 7. Contador de Favoritos (Público) 📊

**Endpoint:** `GET /api/Favorites/service/{searchServiceId}/count`

**Descripción:** Obtiene cuántos usuarios han marcado un servicio como favorito. **NO requiere autenticación**.

**Response Success:**
```json
{
    "success": true,
    "searchServiceId": 123,
    "favoritesCount": 42
}
```

---

## 🎨 Componente React Ejemplo

```jsx
import React, { useState, useEffect } from 'react';

function FavoriteButton({ serviceId }) {
    const [isFavorite, setIsFavorite] = useState(false);
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        checkIfFavorite();
    }, [serviceId]);

    const checkIfFavorite = async () => {
        try {
            const response = await fetch(
                `https://newapi-yn9v.onrender.com/api/Favorites/check/${serviceId}`,
                {
                    headers: {
                        'Authorization': `Bearer ${localStorage.getItem('authToken')}`
                    }
                }
            );
            const data = await response.json();
            setIsFavorite(data.data.isFavorite);
        } catch (error) {
            console.error('Error:', error);
        }
    };

    const toggleFavorite = async () => {
        setLoading(true);
        try {
            const response = await fetch(
                'https://newapi-yn9v.onrender.com/api/Favorites/toggle',
                {
                    method: 'POST',
                    headers: {
                        'Authorization': `Bearer ${localStorage.getItem('authToken')}`,
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({ searchServiceId: serviceId })
                }
            );

            const data = await response.json();
            
            if (data.success) {
                setIsFavorite(data.isFavorite);
            }
        } catch (error) {
            console.error('Error:', error);
        } finally {
            setLoading(false);
        }
    };

    return (
        <button 
            onClick={toggleFavorite} 
            disabled={loading}
            className={`favorite-btn ${isFavorite ? 'active' : ''}`}
        >
            {isFavorite ? '❤️' : '🤍'}
        </button>
    );
}

export default FavoriteButton;
```

---

## 📱 Página de Favoritos Ejemplo

```jsx
import React, { useState, useEffect } from 'react';

function FavoritesPage() {
    const [favorites, setFavorites] = useState([]);
    const [loading, setLoading] = useState(true);
    const [page, setPage] = useState(1);
    const [totalPages, setTotalPages] = useState(1);

    useEffect(() => {
        loadFavorites();
    }, [page]);

    const loadFavorites = async () => {
        setLoading(true);
        try {
            const response = await fetch(
                `https://newapi-yn9v.onrender.com/api/Favorites?page=${page}&pageSize=20`,
                {
                    headers: {
                        'Authorization': `Bearer ${localStorage.getItem('authToken')}`
                    }
                }
            );

            const data = await response.json();
            
            if (data.success) {
                setFavorites(data.data);
                setTotalPages(data.pagination.totalPages);
            }
        } catch (error) {
            console.error('Error:', error);
        } finally {
            setLoading(false);
        }
    };

    if (loading) return <div>Cargando favoritos...</div>;

    if (favorites.length === 0) {
        return (
            <div className="empty-state">
                <h2>No tienes favoritos aún</h2>
                <p>Explora servicios y guarda tus favoritos aquí</p>
            </div>
        );
    }

    return (
        <div className="favorites-page">
            <h1>Mis Favoritos ❤️</h1>
            
            <div className="favorites-grid">
                {favorites.map(fav => (
                    <ServiceCard 
                        key={fav.id} 
                        service={fav.service}
                        onRemove={() => loadFavorites()}
                    />
                ))}
            </div>

            {totalPages > 1 && (
                <div className="pagination">
                    <button 
                        onClick={() => setPage(p => Math.max(1, p - 1))}
                        disabled={page === 1}
                    >
                        Anterior
                    </button>
                    <span>Página {page} de {totalPages}</span>
                    <button 
                        onClick={() => setPage(p => Math.min(totalPages, p + 1))}
                        disabled={page === totalPages}
                    >
                        Siguiente
                    </button>
                </div>
            )}
        </div>
    );
}

export default FavoritesPage;
```

---

## ⚠️ Manejo de Errores

### Errores Comunes

**401 Unauthorized:**
```json
{
    "success": false,
    "message": "Usuario no autenticado"
}
```
→ El token JWT es inválido o no se proporcionó.

**400 Bad Request:**
```json
{
    "success": false,
    "message": "El servicio ya está en favoritos"
}
```
→ Intentaste agregar un favorito que ya existe.

**404 Not Found:**
```json
{
    "success": false,
    "message": "El servicio no está en favoritos"
}
```
→ Intentaste eliminar un favorito que no existe.

---

## 🚀 Migración de Base de Datos

La migración ya está creada. Para aplicarla:

```bash
# Conectar SSH tunnel a Supabase
ssh -i ~/.ssh/id_ed25519 -L 5433:db.rveqsehzlvbttlpmsbmi.supabase.co:5432 Diego@DESKTOP-9LE35LG

# En otra terminal, aplicar migración
dotnet ef database update --no-build
```

---

## 📊 Estructura de Base de Datos

### Tabla: `SearchServiceFavorites`

| Columna | Tipo | Descripción |
|---------|------|-------------|
| `Id` | int | PK, auto-increment |
| `UserId` | int | FK a Users |
| `SearchServiceId` | int | FK a SearchServices |
| `CreatedAt` | timestamp | Fecha de creación |

**Índices:**
- Índice único en `(UserId, SearchServiceId)` - Un usuario no puede marcar el mismo servicio dos veces
- Índice en `UserId` - Para consultas rápidas de favoritos por usuario
- Índice en `SearchServiceId` - Para contar favoritos de un servicio
- Índice en `CreatedAt` - Para ordenar por fecha

---

## ✅ Checklist de Implementación Frontend

- [ ] Agregar botón de corazón en tarjetas de servicios
- [ ] Implementar toggle de favoritos con feedback visual
- [ ] Crear página/pestaña de favoritos
- [ ] Mostrar estado de carga mientras se cargan favoritos
- [ ] Implementar paginación en lista de favoritos
- [ ] Agregar estado vacío cuando no hay favoritos
- [ ] Sincronizar estado de favoritos en toda la app
- [ ] Agregar animaciones al agregar/eliminar favoritos
- [ ] Mostrar contador de favoritos en servicios (opcional)
- [ ] Implementar filtros/búsqueda en favoritos (opcional)

---

## 🎯 Mejores Prácticas

1. **Usa `toggle` en lugar de add/remove** - Mejor UX con un solo click
2. **Verifica múltiples servicios a la vez** - Usa `check-multiple` para listas
3. **Cachea el estado de favoritos** - Reduce llamadas a la API
4. **Feedback visual inmediato** - Actualiza UI antes de la respuesta del servidor
5. **Maneja errores de autenticación** - Redirige al login si el token expira

---

## 📞 Soporte

Para dudas o problemas, contacta al equipo de desarrollo.

**Última actualización:** 9 de enero de 2026
