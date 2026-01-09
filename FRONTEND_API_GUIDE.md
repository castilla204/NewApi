# 📋 Guía Completa para el Frontend - Integración con la API

## 🔍 Problema Actual

El frontend está recibiendo **HTML en lugar de JSON** cuando hace requests a la API. Esto causa el error:
```
Unexpected token '<', <!doctype "... is not valid JSON
```

## ✅ Solución: Configuración Correcta del Frontend

### 1. **URL Base de la API**

**❌ INCORRECTO:**
```javascript
// NO usar directamente inspecciono.com
const API_URL = 'https://inspecciono.com/api';
```

**✅ CORRECTO:**
```javascript
// Usar el backend de Render.com directamente
const API_URL = 'https://newapi-yn9v.onrender.com/api';

// O si tienes un proxy configurado en inspecciono.com:
// Asegúrate de que el proxy reenvíe correctamente a Render.com
const API_URL = 'https://inspecciono.com/api'; // Solo si el proxy está bien configurado
```

### 2. **Headers Requeridos para TODAS las Requests**

```javascript
// ✅ CONFIGURACIÓN CORRECTA DE HEADERS
const headers = {
    'Content-Type': 'application/json',
    'Accept': 'application/json', // ✅ CRÍTICO: Indica que esperas JSON
    // NO incluir 'Accept': '*/*' porque puede causar que el servidor devuelva HTML
};

// Si el endpoint requiere autenticación:
const headersWithAuth = {
    'Content-Type': 'application/json',
    'Accept': 'application/json',
    'Authorization': `Bearer ${token}`, // ✅ Token JWT
};
```

### 3. **Configuración de Fetch/Axios**

#### **Opción A: Usando Fetch (Nativo)**

```javascript
// ✅ FUNCIÓN HELPER CORRECTA
async function apiRequest(endpoint, options = {}) {
    const API_URL = 'https://newapi-yn9v.onrender.com/api';
    const token = localStorage.getItem('authToken'); // O donde guardes el token
    
    const defaultHeaders = {
        'Content-Type': 'application/json',
        'Accept': 'application/json', // ✅ CRÍTICO
    };
    
    // Agregar token si existe
    if (token) {
        defaultHeaders['Authorization'] = `Bearer ${token}`;
    }
    
    const config = {
        method: options.method || 'GET',
        headers: {
            ...defaultHeaders,
            ...options.headers, // Permitir sobrescribir headers
        },
        ...options,
    };
    
    // Agregar body solo si no es GET
    if (options.body && config.method !== 'GET') {
        config.body = JSON.stringify(options.body);
    }
    
    try {
        const response = await fetch(`${API_URL}${endpoint}`, config);
        
        // ✅ VERIFICAR Content-Type ANTES de parsear
        const contentType = response.headers.get('content-type');
        
        if (!contentType || !contentType.includes('application/json')) {
            // Si no es JSON, leer como texto para ver qué devolvió
            const text = await response.text();
            console.error('❌ La API devolvió HTML en lugar de JSON:', text.substring(0, 200));
            throw new Error(`Expected JSON but got ${contentType}. Response: ${text.substring(0, 100)}`);
        }
        
        const data = await response.json();
        
        // Verificar si hay errores en la respuesta
        if (!response.ok) {
            throw new Error(data.message || `HTTP ${response.status}`);
        }
        
        return data;
    } catch (error) {
        console.error('❌ Error en API request:', error);
        throw error;
    }
}

// ✅ USO CORRECTO
async function getHomepageWall(categoryId, latitude, longitude) {
    try {
        const data = await apiRequest(
            `/SearchService/homepage-wall?categoryid=${categoryId}&latitude=${latitude}&longitude=${longitude}&countryCode=ES&locationRange=50&nearbyPage=1&nearbyPageSize=20&popularPage=1&popularPageSize=20`
        );
        return data;
    } catch (error) {
        console.error('Error al cargar servicios:', error.message);
        throw error;
    }
}
```

#### **Opción B: Usando Axios (Recomendado)**

```javascript
import axios from 'axios';

// ✅ CONFIGURACIÓN GLOBAL DE AXIOS
const apiClient = axios.create({
    baseURL: 'https://newapi-yn9v.onrender.com/api',
    headers: {
        'Content-Type': 'application/json',
        'Accept': 'application/json', // ✅ CRÍTICO
    },
    timeout: 90000, // 90 segundos (mismo que el backend)
});

// ✅ INTERCEPTOR PARA AGREGAR TOKEN AUTOMÁTICAMENTE
apiClient.interceptors.request.use(
    (config) => {
        const token = localStorage.getItem('authToken');
        if (token) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => {
        return Promise.reject(error);
    }
);

// ✅ INTERCEPTOR PARA VERIFICAR RESPUESTAS
apiClient.interceptors.response.use(
    (response) => {
        // Verificar que la respuesta sea JSON
        const contentType = response.headers['content-type'];
        if (!contentType || !contentType.includes('application/json')) {
            console.error('❌ La API devolvió HTML en lugar de JSON');
            throw new Error('Expected JSON but got HTML');
        }
        return response;
    },
    (error) => {
        // Manejar errores
        if (error.response) {
            // El servidor respondió con un código de error
            const { status, data } = error.response;
            console.error(`❌ Error ${status}:`, data);
            
            // Si recibimos HTML en lugar de JSON
            if (typeof data === 'string' && data.includes('<!doctype')) {
                console.error('❌ El servidor devolvió HTML en lugar de JSON');
                throw new Error('Server returned HTML instead of JSON. Check proxy configuration.');
            }
        } else if (error.request) {
            // La request se hizo pero no hubo respuesta
            console.error('❌ No se recibió respuesta del servidor');
        } else {
            // Error al configurar la request
            console.error('❌ Error al configurar la request:', error.message);
        }
        throw error;
    }
);

// ✅ USO CORRECTO
async function getHomepageWall(categoryId, latitude, longitude) {
    try {
        const response = await apiClient.get('/SearchService/homepage-wall', {
            params: {
                categoryid: categoryId,
                latitude: latitude,
                longitude: longitude,
                countryCode: 'ES',
                locationRange: 50,
                nearbyPage: 1,
                nearbyPageSize: 20,
                popularPage: 1,
                popularPageSize: 20,
            },
        });
        return response.data;
    } catch (error) {
        console.error('Error al cargar servicios:', error.message);
        throw error;
    }
}
```

### 4. **Endpoints Públicos vs Protegidos**

#### **✅ Endpoints PÚBLICOS (No requieren token):**
- `GET /api/Categories` - Obtener categorías
- `GET /api/ServiceType/public` - Obtener tipos de servicio públicos
- `GET /api/SearchService/homepage-wall` - Muro de homepage
- `GET /api/SearchService/services-in-bounds` - Servicios en área
- `GET /api/SearchService/map-experts` - Expertos en mapa

#### **🔐 Endpoints PROTEGIDOS (Requieren token JWT):**
- `GET /api/User/*` - Información de usuario
- `POST /api/SearchHire/*` - Contratar servicios
- `GET /api/Search/*` - Búsquedas del usuario
- Cualquier endpoint con `[Authorize]`

### 5. **Manejo de Errores Correcto**

```javascript
// ✅ MANEJO COMPLETO DE ERRORES
async function handleApiCall(apiFunction) {
    try {
        const data = await apiFunction();
        return { success: true, data };
  } catch (error) {
        // Verificar si es un error de HTML
        if (error.message.includes('HTML') || error.message.includes('<!doctype')) {
            return {
                success: false,
                error: 'El servidor devolvió HTML en lugar de JSON. Verifica la configuración del proxy.',
                type: 'PROXY_ERROR',
            };
        }
        
        // Verificar si es un error de autenticación
        if (error.response?.status === 401) {
            // Token inválido o expirado
            localStorage.removeItem('authToken');
            return {
                success: false,
                error: 'Sesión expirada. Por favor, inicia sesión nuevamente.',
                type: 'AUTH_ERROR',
            };
        }
        
        // Error genérico
        return {
            success: false,
            error: error.message || 'Error desconocido',
            type: 'API_ERROR',
        };
    }
}
```

### 6. **Verificaciones que DEBE Hacer el Frontend**

#### **✅ Checklist Antes de Hacer una Request:**

```javascript
function validateRequestConfig(endpoint, requiresAuth = false) {
    const errors = [];
    
    // 1. Verificar que la URL base esté configurada
    if (!API_URL) {
        errors.push('❌ API_URL no está configurada');
    }
    
    // 2. Verificar que el endpoint comience con /
    if (!endpoint.startsWith('/')) {
        errors.push('❌ El endpoint debe comenzar con /');
    }
    
    // 3. Verificar token si es requerido
    if (requiresAuth) {
        const token = localStorage.getItem('authToken');
        if (!token) {
            errors.push('❌ Token de autenticación no encontrado');
        }
    }
    
    // 4. Verificar headers
    const headers = {
        'Content-Type': 'application/json',
        'Accept': 'application/json', // ✅ CRÍTICO
    };
    
    if (errors.length > 0) {
        console.error('❌ Errores de configuración:', errors);
        return false;
    }
    
    return true;
}
```

#### **✅ Verificaciones Después de Recibir la Respuesta:**

```javascript
function validateResponse(response) {
    // 1. Verificar Content-Type
    const contentType = response.headers.get('content-type');
    if (!contentType || !contentType.includes('application/json')) {
        console.error('❌ Content-Type incorrecto:', contentType);
        return false;
    }
    
    // 2. Verificar que la respuesta sea un objeto (JSON parseado)
    if (typeof response.data !== 'object') {
        console.error('❌ La respuesta no es un objeto JSON:', typeof response.data);
        return false;
    }
    
    // 3. Verificar status code
    if (response.status >= 400) {
        console.error('❌ Status code de error:', response.status);
        return false;
    }
    
    return true;
}
```

### 7. **Ejemplo Completo de Implementación**

```javascript
// ✅ CONFIGURACIÓN COMPLETA PARA REACT/VUE/ANGULAR

// api/config.js
export const API_CONFIG = {
    BASE_URL: 'https://newapi-yn9v.onrender.com/api',
    TIMEOUT: 90000, // 90 segundos
    HEADERS: {
        'Content-Type': 'application/json',
        'Accept': 'application/json', // ✅ CRÍTICO
    },
};

// api/client.js
import axios from 'axios';
import { API_CONFIG } from './config';

const apiClient = axios.create({
    baseURL: API_CONFIG.BASE_URL,
    timeout: API_CONFIG.TIMEOUT,
    headers: API_CONFIG.HEADERS,
});

// Interceptor para agregar token
apiClient.interceptors.request.use(
    (config) => {
        const token = localStorage.getItem('authToken');
        if (token) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => Promise.reject(error)
);

// Interceptor para verificar respuestas
apiClient.interceptors.response.use(
    (response) => {
        // Verificar Content-Type
        const contentType = response.headers['content-type'];
        if (!contentType?.includes('application/json')) {
            const error = new Error('Expected JSON but got ' + contentType);
            error.response = response;
            return Promise.reject(error);
        }
        return response;
    },
    (error) => {
        // Manejar errores
        if (error.response?.data && typeof error.response.data === 'string') {
            if (error.response.data.includes('<!doctype')) {
                console.error('❌ El servidor devolvió HTML en lugar de JSON');
                error.message = 'Server returned HTML instead of JSON';
            }
        }
        return Promise.reject(error);
    }
);

// api/services/searchService.js
import apiClient from '../client';

export const searchService = {
    // ✅ Endpoint público - NO requiere token
    async getHomepageWall(params) {
        try {
            const response = await apiClient.get('/SearchService/homepage-wall', {
                params: {
                    categoryid: params.categoryId,
                    latitude: params.latitude,
                    longitude: params.longitude,
                    countryCode: params.countryCode || 'ES',
                    locationRange: params.locationRange || 50,
                    nearbyPage: params.nearbyPage || 1,
                    nearbyPageSize: params.nearbyPageSize || 20,
                    popularPage: params.popularPage || 1,
                    popularPageSize: params.popularPageSize || 20,
                },
            });
            return response.data;
        } catch (error) {
            console.error('Error al obtener homepage wall:', error);
            throw error;
        }
    },
    
    // ✅ Endpoint público
    async getCategories() {
        try {
            const response = await apiClient.get('/Categories');
            return response.data;
        } catch (error) {
            console.error('Error al obtener categorías:', error);
            throw error;
        }
    },
    
    // ✅ Endpoint público
    async getServiceTypes() {
        try {
            const response = await apiClient.get('/ServiceType/public');
            return response.data;
        } catch (error) {
            console.error('Error al obtener tipos de servicio:', error);
            throw error;
        }
    },
};

// Uso en componente
import { searchService } from '@/api/services/searchService';

async function loadHomepageData() {
    try {
        const data = await searchService.getHomepageWall({
            categoryId: 1,
            latitude: 42.45569,
            longitude: -2.4715712,
            countryCode: 'ES',
        });
        console.log('✅ Datos cargados:', data);
        return data;
    } catch (error) {
        console.error('❌ Error:', error.message);
        // Mostrar mensaje de error al usuario
        showError('Error al cargar servicios. Por favor, intenta nuevamente.');
    }
}
```

### 8. **Debugging: Cómo Verificar que Todo Está Bien**

```javascript
// ✅ FUNCIÓN DE DEBUG PARA VERIFICAR CONFIGURACIÓN
function debugApiConfiguration() {
    console.log('🔍 Verificando configuración de API...');
    
    // 1. Verificar URL base
    console.log('📍 API URL:', API_CONFIG.BASE_URL);
    
    // 2. Verificar headers
    console.log('📋 Headers configurados:', API_CONFIG.HEADERS);
    
    // 3. Verificar token
    const token = localStorage.getItem('authToken');
    console.log('🔑 Token presente:', token ? 'Sí' : 'No');
    if (token) {
        console.log('🔑 Token (primeros 20 chars):', token.substring(0, 20) + '...');
    }
    
    // 4. Hacer una request de prueba
    apiClient.get('/Categories')
        .then(response => {
            console.log('✅ Request de prueba exitosa:', response.status);
            console.log('✅ Content-Type:', response.headers['content-type']);
            console.log('✅ Datos recibidos:', typeof response.data);
        })
        .catch(error => {
            console.error('❌ Request de prueba falló:', error.message);
            if (error.response) {
                console.error('❌ Status:', error.response.status);
                console.error('❌ Headers:', error.response.headers);
                console.error('❌ Data:', error.response.data);
            }
        });
}

// Llamar en desarrollo
if (process.env.NODE_ENV === 'development') {
    debugApiConfiguration();
}
```

## 🚨 Errores Comunes y Soluciones

### Error 1: "Unexpected token '<', <!doctype..."
**Causa:** El servidor está devolviendo HTML en lugar de JSON
**Solución:**
1. Verificar que `Accept: application/json` esté en los headers
2. Verificar que la URL base apunte a `newapi-yn9v.onrender.com`
3. Verificar la configuración del proxy si usas `inspecciono.com`

### Error 2: "401 Unauthorized"
**Causa:** Token faltante, inválido o expirado
**Solución:**
1. Verificar que el token esté en `localStorage`
2. Verificar que el header `Authorization: Bearer <token>` esté presente
3. Renovar el token si está expirado

### Error 3: "CORS error"
**Causa:** El servidor no permite requests desde tu dominio
**Solución:**
1. Verificar que el origen esté permitido en el backend
2. Verificar que los headers CORS estén configurados correctamente

## 📝 Resumen de Puntos Críticos

1. ✅ **SIEMPRE** incluir `Accept: application/json` en los headers
2. ✅ **SIEMPRE** verificar `Content-Type` antes de parsear la respuesta
3. ✅ **SIEMPRE** usar la URL base correcta (`newapi-yn9v.onrender.com`)
4. ✅ **SIEMPRE** agregar el token JWT para endpoints protegidos
5. ✅ **SIEMPRE** manejar errores y verificar que la respuesta sea JSON

## 🔗 Endpoints Disponibles

### Públicos (No requieren autenticación):
- `GET /api/Categories`
- `GET /api/ServiceType/public`
- `GET /api/SearchService/homepage-wall`
- `GET /api/SearchService/services-in-bounds`
- `GET /api/SearchService/map-experts`

### Protegidos (Requieren token JWT):
- `GET /api/User/*`
- `POST /api/SearchHire/*`
- `GET /api/Search/*`

---

**Última actualización:** 2026-01-07
**Versión API:** 1.0.0
