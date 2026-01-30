# 🔍 ANÁLISIS COMPLETO: Problema de Desregistro Automático en Web

## 📋 RESUMEN DEL PROBLEMA

**Síntoma**: En web, muchas veces al entrar, el usuario se ha desregistrado solo (se pierde la sesión automáticamente).

**Impacto**: El usuario tiene que volver a hacer login constantemente, incluso cuando no ha cerrado sesión manualmente.

---

## 🎯 POSIBLES CAUSAS IDENTIFICADAS

### 1. **Validación de Token Muy Estricta en `auth.ts`**
El archivo `lib/auth.ts` tiene una validación que **limpia automáticamente** el token si:
- El token no tiene formato JWT válido (3 partes separadas por `.`)
- El token está expirado
- Hay un error al parsear el token

**Código problemático:**
```typescript
// lib/auth.ts líneas 47-68
export function getAuthToken(): string | null {
    const token = localStorage.getItem('authToken');
    if (!token) return null;

    try {
        const parts = token.split('.');
        if (parts.length !== 3) {
            // ❌ PROBLEMA: Limpia el token si no tiene formato JWT
            removeAuthToken();
            return null;
        }

        const payload = JSON.parse(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/')));
        if (payload.exp && payload.exp * 1000 < Date.now()) {
            // ❌ PROBLEMA: Limpia el token si está expirado (pero debería renovarlo)
            removeAuthToken();
            return null;
        }

        return token;
    } catch (error) {
        // ❌ PROBLEMA: Limpia el token si hay cualquier error al parsear
        removeAuthToken();
        return null;
    }
}
```

**Problema**: Esta función se llama en múltiples lugares y **siempre limpia el token** en lugar de intentar renovarlo con el refresh token.

---

### 2. **Doble Sistema de Tokens (Inconsistencia)**

El sistema tiene **DOS sistemas de tokens** que pueden entrar en conflicto:

**Sistema 1: `authService.ts`** (Nuevo, más completo)
- Usa `accessToken` y `refreshToken` en localStorage
- Tiene renovación automática
- Tiene interceptor de fetch

**Sistema 2: `lib/auth.ts`** (Antiguo, para compatibilidad)
- Usa `authToken` en localStorage
- **NO tiene renovación automática**
- Limpia tokens sin intentar renovarlos

**Problema**: `AuthContext.tsx` usa `getAuthToken()` de `lib/auth.ts`, que puede limpiar tokens que `authService.ts` considera válidos.

---

### 3. **AuthContext Restaura Sesión Pero No Sincroniza con authService**

En `AuthContext.tsx`:
- Restaura sesión desde localStorage
- Verifica tokens
- **PERO** no siempre sincroniza con `authService`

**Código problemático:**
```typescript
// AuthContext.tsx líneas 80-90
const storedUserData = getUserData();
if (!storedUserData) {
    console.log('⚠️ [AuthContext] No hay userData en localStorage');
    // ❌ PROBLEMA: Limpia tokens sin verificar si authService los tiene
    setUser(null);
    setIsAuthenticated(false);
    removeAuthToken(); // Esto limpia authToken pero NO accessToken/refreshToken
    setIsLoading(false);
    return;
}
```

---

### 4. **Validación de UserData Muy Estricta**

En `lib/auth.ts`, la función `getUserData()` limpia los datos si:
- No encuentra `user` o `userData` en localStorage
- El usuario no tiene `id` válido
- El usuario no tiene `email`

**Código problemático:**
```typescript
// lib/auth.ts líneas 94-108
if (!parsedData || !hasId || !hasEmail) {
    console.log('⚠️ [auth.ts] Datos de usuario inválidos:', { 
        userId,
        hasId, 
        hasEmail, 
        hasName,
        parsedDataKeys: parsedData ? Object.keys(parsedData) : 'null',
        parsedDataId: parsedData?.Id || parsedData?.id,
        parsedDataEmail: parsedData?.Email || parsedData?.email
    });
    // ❌ PROBLEMA: Limpia datos sin intentar recuperarlos del backend
    localStorage.removeItem('user');
    localStorage.removeItem('userData');
    return null;
}
```

---

### 5. **Token Refresh Falla Silenciosamente**

En `authService.ts`, cuando el refresh token falla:
```typescript
// authService.ts líneas 193-199
if (response.status === 401) {
    // ✅ Refresh token inválido o expirado → Solo limpiar tokens, no redirigir inmediatamente
    console.warn('[AuthService] Refresh token inválido o expirado');
    this.clearTokens(); // ❌ PROBLEMA: Limpia tokens sin notificar al usuario
    return false;
}
```

**Problema**: Si el refresh token expira (después de 7 días según el backend), limpia todos los tokens sin dar opción al usuario de renovar la sesión.

---

## 📁 ARCHIVOS COMPLETOS DEL SISTEMA DE AUTENTICACIÓN

### **FRONTEND - ReactWeb**

#### 1. `src/services/authService.ts`
```typescript
import { API_CONFIG } from '../config/api';
import { jwtDecode } from 'jwt-decode';

interface TokenPair {
    accessToken: string;
    refreshToken: string;
}

interface GoogleAuthResponse {
    token: string; // Formato: "accessToken|refreshToken"
    user: any;
    requiresMFA?: boolean;
}

class AuthService {
    private accessToken: string | null = null;
    private refreshToken: string | null = null;
    private refreshTimeout: NodeJS.Timeout | null = null;
    // ✅ BEST PRACTICE: Prevenir race conditions en token refresh
    private refreshPromise: Promise<boolean> | null = null;
    // ✅ Cola de requests pendientes esperando verificación MFA
    private pendingMfaRequests: Array<{ url: string; options: RequestInit; resolve: (response: Response) => void; reject: (error: any) => void }> = [];

    constructor() {
        this.initFromStorage();
        this.setupAxiosInterceptor();
    }

    // ============================================
    // 1. GOOGLE AUTH (Login)
    // ============================================
    async googleAuth(googleCredential: string): Promise<{ success: boolean; user: any; requiresMFA: boolean }> {
        try {
            const decoded: any = jwtDecode(googleCredential);

            const response = await fetch(`${API_CONFIG.baseUrl}${API_CONFIG.endpoints.auth.googleAuth}`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Accept': 'application/json',
                },
                body: JSON.stringify({
                    accessToken: googleCredential,
                    email: decoded.email,
                    name: decoded.name,
                    googleId: decoded.sub,
                }),
            });

            // ✅ BEST PRACTICE: Manejar diferentes tipos de errores
            if (!response.ok) {
                let errorMessage = 'Authentication failed';
                
                if (response.status === 429) {
                    errorMessage = 'Too many requests. Please wait a moment before trying again.';
                } else if (response.status === 403) {
                    errorMessage = 'Google OAuth configuration error. Please contact administrator.';
                } else {
                    // Intentar obtener mensaje del servidor
                    try {
                        const errorData = await response.json();
                        errorMessage = errorData.message || errorData.error || errorMessage;
                        
                        // ✅ Mejorar mensaje para errores de 'aud' claim
                        if (errorData.details && errorData.details.includes("untrusted 'aud' claim")) {
                            errorMessage = 'Error de configuración: El Client ID de Google OAuth no coincide entre el frontend y el backend. Por favor contacta al administrador.';
                            console.error('[AuthService] Google OAuth Client ID mismatch:', {
                                frontendClientId: '61603823707-4vsp43naifci8t893hdc276kkhbvn49a.apps.googleusercontent.com',
                                error: errorData
                            });
                        }
                    } catch {
                        // Si no se puede parsear JSON, usar mensaje por defecto
                        errorMessage = `Authentication failed (${response.status})`;
                    }
                }
                
                throw new Error(errorMessage);
            }

            const data: GoogleAuthResponse = await response.json();

            if (!data.token || !data.user) {
                throw new Error('Invalid response from server');
            }

            // ✅ CRÍTICO: El backend devuelve "accessToken|refreshToken"
            const [accessToken, refreshToken] = data.token.split('|');

            if (!accessToken || !refreshToken) {
                throw new Error('Invalid token format from server');
            }

            this.setTokens(accessToken, refreshToken);

            // Iniciar auto-renovación
            this.scheduleTokenRefresh();

            return {
                success: true,
                user: data.user,
                requiresMFA: data.requiresMFA || false,
            };
        } catch (error: any) {
            console.error('Google Auth error:', error);
            throw error;
        }
    }

    // ============================================
    // 2. GUARDAR TOKENS
    // ============================================
    setTokens(accessToken: string, refreshToken: string) {
        this.accessToken = accessToken;
        this.refreshToken = refreshToken;

        localStorage.setItem('accessToken', accessToken);
        localStorage.setItem('refreshToken', refreshToken);

        // Guardar también en el formato antiguo para compatibilidad
        localStorage.setItem('authToken', accessToken);
    }

    getAccessToken(): string | null {
        return this.accessToken || localStorage.getItem('accessToken');
    }

    getRefreshToken(): string | null {
        return this.refreshToken || localStorage.getItem('refreshToken');
    }

    // ============================================
    // 3. RENOVAR ACCESS TOKEN AUTOMÁTICAMENTE
    // ============================================
    scheduleTokenRefresh() {
        // ✅ BEST PRACTICE: Limpiar timeout anterior para evitar memory leaks
        if (this.refreshTimeout) {
            clearTimeout(this.refreshTimeout);
            this.refreshTimeout = null;
        }

        try {
            const token = this.getAccessToken();
            if (!token) return;

            const decoded: any = jwtDecode(token);
            const expiresIn = decoded.exp * 1000 - Date.now();

            // ✅ BEST PRACTICE: Renovar 2 minutos antes de expirar (Access Token dura 30 min)
            // Esto asegura que el token nunca expire durante una sesión activa
            const refreshIn = Math.max(0, expiresIn - (2 * 60 * 1000));

            if (refreshIn > 0) {
                // Solo loguear en desarrollo
                if (import.meta.env.DEV) {
                    console.log(`[AuthService] Token expira en ${Math.floor(expiresIn / 1000 / 60)} minutos. Renovando en ${Math.floor(refreshIn / 1000 / 60)} minutos.`);
                }
                
                this.refreshTimeout = setTimeout(() => {
                    this.refreshAccessToken();
                }, refreshIn);
            } else {
                // Token ya expiró o está a punto de expirar, renovar inmediatamente
                this.refreshAccessToken();
            }
        } catch (error) {
            console.error('[AuthService] Error scheduling token refresh:', error);
        }
    }

    async refreshAccessToken(): Promise<boolean> {
        // ✅ BEST PRACTICE: Prevenir múltiples refresh simultáneos (race condition)
        if (this.refreshPromise) {
            return this.refreshPromise;
        }

        this.refreshPromise = (async () => {
            try {
                const refreshToken = this.getRefreshToken();
                if (!refreshToken) {
                    throw new Error('No refresh token available');
                }

                const response = await fetch(`${API_CONFIG.baseUrl}${API_CONFIG.endpoints.auth.refreshToken}`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify({ refreshToken }),
                });

                if (!response.ok) {
                    if (response.status === 401) {
                        // ✅ Refresh token inválido o expirado → Solo limpiar tokens, no redirigir inmediatamente
                        // Esto permite que el usuario vea el error en lugar de ser redirigido automáticamente
                        console.warn('[AuthService] Refresh token inválido o expirado');
                        this.clearTokens();
                        // No redirigir automáticamente - dejar que los componentes manejen el error
                        return false;
                    }
                    throw new Error('Failed to refresh token');
                }

                const data = await response.json();

                if (data.accessToken && data.refreshToken) {
                    // ✅ Backend devuelve NUEVOS access y refresh tokens (rotación)
                    this.setTokens(data.accessToken, data.refreshToken);

                    // Programar próxima renovación
                    this.scheduleTokenRefresh();

                    console.log('✅ Token renovado exitosamente');
                    return true;
                }

                return false;
            } catch (error) {
                console.error('Error refreshing token:', error);
                this.logout();
                return false;
            } finally {
                // Limpiar promise después de un delay para permitir que otros requests la reutilicen
                setTimeout(() => {
                    this.refreshPromise = null;
                }, 1000);
            }
        })();

        return this.refreshPromise;
    }

    // ============================================
    // 4. INTERCEPTOR PARA AUTO-RENOVACIÓN EN 401
    // ============================================
    setupAxiosInterceptor() {
        // Interceptar fetch requests
        const originalFetch = window.fetch;
        const self = this;
        
        window.fetch = async function(...args) {
            const [url, options = {}] = args;
            const fetchOptions: RequestInit = { ...options };

            // ✅ CRÍTICO: Solo agregar token si NO es un endpoint público
            // Los endpoints públicos no necesitan autenticación y agregar token puede causar delays
            const publicEndpoints = [
                '/api/Categories',
                '/api/ServiceType/public',
                '/api/SearchService/homepage-wall',
                '/health',
                '/warmup'
            ];
            const urlString = typeof url === 'string' ? url : url.toString();
            const isPublic = publicEndpoints.some(endpoint => urlString.includes(endpoint));

            // Agregar token solo si NO es un endpoint público
            const token = self.getAccessToken();
            if (token && !isPublic) {
                const headers = new Headers(fetchOptions.headers);
                if (!headers.has('Authorization')) {
                    headers.set('Authorization', `Bearer ${token}`);
                }
                fetchOptions.headers = headers;
            }

            let response = await originalFetch(url, fetchOptions);

            // ✅ Si recibimos 403 por MFA → Manejar según el tipo
            if (response.status === 403 && !(fetchOptions as any)._mfaChecked) {
                try {
                    // ✅ Verificar que la respuesta sea JSON antes de parsear
                    const contentType = response.headers.get('content-type');
                    if (!contentType || !contentType.includes('application/json')) {
                        // Si no es JSON, devolver la respuesta sin procesar
                        return response;
                    }
                    
                    const clonedResponse = response.clone();
                    const text = await clonedResponse.text();
                    
                    // Verificar si es HTML
                    if (text.trim().toLowerCase().startsWith('<!doctype') || text.trim().toLowerCase().startsWith('<html')) {
                        // Es HTML, no JSON - devolver respuesta sin procesar
                        return response;
                    }
                    
                    const data = JSON.parse(text);
                    
                    // ✅ MFA_VERIFICATION_REQUIRED: MFA habilitado pero no verificado → Mostrar verificación
                    if (data.error === 'MFA_VERIFICATION_REQUIRED') {
                        (fetchOptions as any)._mfaChecked = true;
                        console.warn('[AuthService] MFA verification required, showing verification modal');
                        
                        // Guardar request en cola y mostrar modal
                        return new Promise<Response>((resolve, reject) => {
                            self.pendingMfaRequests.push({
                                url: url as string,
                                options: fetchOptions,
                                resolve,
                                reject
                            });
                            
                            // Disparar evento para mostrar modal de verificación
                            const mfaVerificationEvent = new CustomEvent('showMfaVerification', {
                                detail: {
                                    onSuccess: async () => {
                                        // Después de verificación exitosa, reintentar todos los requests pendientes
                                        await self.retryPendingMfaRequests();
                                    }
                                }
                            });
                            window.dispatchEvent(mfaVerificationEvent);
                        });
                    }
                } catch {
                    // Si no se puede parsear JSON, continuar normalmente
                }
            }
            
            // Si recibimos 401, intentar renovar token (solo si tenemos refresh token)
            // ✅ EXCEPCIÓN: No intentar renovar token para verifyMFA porque un 401 puede ser código inválido, no token expirado
            const isMfaVerifyEndpoint = typeof url === 'string' && url.includes('/api/auth/mfa/verify');
            if (response.status === 401 && !(fetchOptions as any)._retry && !isMfaVerifyEndpoint) {
                const refreshToken = self.getRefreshToken();
                
                // Solo intentar refrescar si tenemos refresh token disponible
                if (refreshToken) {
                    (fetchOptions as any)._retry = true;

                    try {
                        // ✅ BEST PRACTICE: refreshAccessToken ya maneja race conditions
                        const success = await self.refreshAccessToken();

                        if (success) {
                            // Reintentar request original con nuevo token
                            const newToken = self.getAccessToken();
                            if (newToken) {
                                const headers = new Headers(fetchOptions.headers);
                                headers.set('Authorization', `Bearer ${newToken}`);
                                fetchOptions.headers = headers;
                            }
                            response = await originalFetch(url, fetchOptions);
                        }
                    } catch (error) {
                        // Si falla el refresh, no hacer nada (el 401 original se devuelve)
                        console.debug('Token refresh failed, returning original 401 response');
                    }
                } else {
                    // No hay refresh token, no intentar refrescar
                    // Esto es normal cuando el usuario no está autenticado
                }
            }

            return response;
        };
    }

    // ============================================
    // 5. REINTENTAR REQUESTS PENDIENTES DESPUÉS DE MFA
    // ============================================
    private async retryPendingMfaRequests() {
        const requests = [...this.pendingMfaRequests];
        this.pendingMfaRequests = [];
        
        const originalFetch = window.fetch;
        const newToken = this.getAccessToken();
        
        for (const request of requests) {
            try {
                if (newToken) {
                    const headers = new Headers(request.options.headers);
                    headers.set('Authorization', `Bearer ${newToken}`);
                    request.options.headers = headers;
                }
                (request.options as any)._mfaChecked = false; // Permitir reintento
                const response = await originalFetch(request.url, request.options);
                request.resolve(response);
            } catch (error) {
                request.reject(error);
            }
        }
    }

    // ============================================
    // 6. LOGOUT
    // ============================================
    async logout() {
        try {
            const refreshToken = this.getRefreshToken();
            if (refreshToken) {
                await fetch(`${API_CONFIG.baseUrl}${API_CONFIG.endpoints.auth.logout}`, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify({ refreshToken }),
                });
            }
        } catch (error) {
            console.error('Logout error:', error);
        } finally {
            this.clearTokens();
        }
    }

    async logoutAllDevices() {
        try {
            await fetch(`${API_CONFIG.baseUrl}${API_CONFIG.endpoints.auth.revokeAll}`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': `Bearer ${this.getAccessToken()}`,
                },
            });
            this.clearTokens();
        } catch (error) {
            console.error('Logout all devices error:', error);
        }
    }

    clearTokens() {
        this.accessToken = null;
        this.refreshToken = null;

        localStorage.removeItem('accessToken');
        localStorage.removeItem('refreshToken');
        localStorage.removeItem('authToken');
        localStorage.removeItem('userData');

        if (this.refreshTimeout) {
            clearTimeout(this.refreshTimeout);
            this.refreshTimeout = null;
        }
    }

    // ============================================
    // 6. INICIALIZAR AL CARGAR LA APP
    // ============================================
    initFromStorage() {
        const accessToken = localStorage.getItem('accessToken');
        const refreshToken = localStorage.getItem('refreshToken');

        if (accessToken && refreshToken) {
            this.accessToken = accessToken;
            this.refreshToken = refreshToken;

            // ✅ BEST PRACTICE: Verificar si el access token ya expiró
            try {
                const decoded: any = jwtDecode(accessToken);
                const expirationTime = decoded.exp * 1000;
                const now = Date.now();
                const isExpired = expirationTime < now;
                const timeUntilExpiry = expirationTime - now;

                if (isExpired) {
                    // Token expirado, renovar inmediatamente
                    if (import.meta.env.DEV) {
                        console.log('[AuthService] Token expired, refreshing immediately');
                    }
                    this.refreshAccessToken();
                } else if (timeUntilExpiry < 2 * 60 * 1000) {
                    // Token expira en menos de 2 minutos, renovar ahora
                    if (import.meta.env.DEV) {
                        console.log('[AuthService] Token expires soon, refreshing immediately');
                    }
                    this.refreshAccessToken();
                } else {
                    // Programar renovación
                    this.scheduleTokenRefresh();
                }
            } catch (error) {
                console.error('[AuthService] Invalid token:', error);
                this.clearTokens();
            }
        }
    }

    // ============================================
    // 7. HELPERS
    // ============================================
    isAuthenticated(): boolean {
        return !!this.accessToken && !!this.refreshToken;
    }

    getCurrentUser(): any | null {
        if (!this.accessToken) return null;

        try {
            return jwtDecode(this.accessToken) as any;
        } catch (error) {
            return null;
        }
    }
}

// Exportar instancia singleton
export const authService = new AuthService();
```

#### 2. `src/lib/auth.ts`
```typescript
import { authService } from '../services/authService';

// Mantener compatibilidad con código existente
export async function authenticateWithGoogle(accessToken: string, email: string, name: string, googleId: string) {
    const result = await authService.googleAuth(accessToken);
    return {
        token: `${result.user ? 'token' : ''}`, // Mantener compatibilidad
        user: result.user,
        requiresMFA: result.requiresMFA,
    };
}

export function setAuthToken(token: string, user?: any) {
    localStorage.setItem('authToken', token);
    if (user) {
        // Asegurarse de guardar el usuario con las propiedades correctas (puede venir con Email/email, Role/role, Id/id)
        const userToStore = {
            ...user,
            // Normalizar propiedades a minúsculas para compatibilidad
            id: user.Id || user.id,
            email: user.Email || user.email,
            role: user.Role || user.role,
            name: user.Name || user.name,
            // Mantener también las originales por si acaso
            Id: user.Id || user.id,
            Email: user.Email || user.email,
            Role: user.Role || user.role,
            Name: user.Name || user.name,
        };
        // ✅ Guardar en ambas claves para compatibilidad: 'user' (guía) y 'userData' (código existente)
        localStorage.setItem('user', JSON.stringify(userToStore));
        localStorage.setItem('userData', JSON.stringify(userToStore));
        console.log('✅ [auth.ts] Token y user guardados:', { 
            hasToken: !!token, 
            hasUser: !!user,
            userEmail: userToStore.email || userToStore.Email,
            userRole: userToStore.role || userToStore.Role
        });
    }
}

export function getAuthToken(): string | null {
    const token = localStorage.getItem('authToken');
    if (!token) return null;

    try {
        // Verificar si el token tiene el formato JWT básico
        const parts = token.split('.');
        if (parts.length !== 3) {
            // Token con formato inválido - limpiar
            removeAuthToken();
            return null;
        }

        // Decodificar el payload para verificar expiración
        const payload = JSON.parse(atob(parts[1].replace(/-/g, '+').replace(/_/g, '/')));
        if (payload.exp && payload.exp * 1000 < Date.now()) {
            // Token expirado - limpiar
            removeAuthToken();
            return null;
        }

        return token;
    } catch (error) {
        // Error al parsear token - limpiar
        removeAuthToken();
        return null; // No eliminamos el token automáticamente
    }
}

export function removeAuthToken() {
    localStorage.removeItem('authToken');
    localStorage.removeItem('user');
    localStorage.removeItem('userData');
}

export function getUserData(): any | null {
    try {
        // ✅ Intentar primero 'user' (guía), luego 'userData' (compatibilidad)
        let userData = localStorage.getItem('user') || localStorage.getItem('userData');
        if (!userData) {
            console.log('⚠️ [auth.ts] No user/userData encontrado en localStorage');
            return null;
        }

        const parsedData = JSON.parse(userData);
        
        // ✅ Verificar que tenga al menos id (puede ser Id o id) y email (puede ser Email o email)
        const userId = parsedData?.Id || parsedData?.id;
        const hasId = userId && !isNaN(userId);
        const hasEmail = parsedData?.Email || parsedData?.email;
        const hasName = parsedData?.Name || parsedData?.name;
        
        if (!parsedData || !hasId || !hasEmail) {
            console.log('⚠️ [auth.ts] Datos de usuario inválidos:', { 
                userId,
                hasId, 
                hasEmail, 
                hasName,
                parsedDataKeys: parsedData ? Object.keys(parsedData) : 'null',
                parsedDataId: parsedData?.Id || parsedData?.id,
                parsedDataEmail: parsedData?.Email || parsedData?.email
            });
            // Datos de usuario inválidos - limpiar ambas claves
            localStorage.removeItem('user');
            localStorage.removeItem('userData');
            return null;
        }
        
        // ✅ Normalizar id (asegurar que exista tanto Id como id)
        if (!parsedData.id && parsedData.Id) parsedData.id = parsedData.Id;
        if (!parsedData.Id && parsedData.id) parsedData.Id = parsedData.id;

        // Asegurarse de que phoneVerified sea un booleano
        parsedData.phoneVerified = Boolean(parsedData.phoneVerified || parsedData.PhoneVerified);
        
        // Normalizar propiedades para compatibilidad (mantener ambas versiones)
        if (!parsedData.email && parsedData.Email) parsedData.email = parsedData.Email;
        if (!parsedData.Email && parsedData.email) parsedData.Email = parsedData.email;
        if (!parsedData.role && parsedData.Role) parsedData.role = parsedData.Role;
        if (!parsedData.Role && parsedData.role) parsedData.Role = parsedData.role;
        if (!parsedData.name && parsedData.Name) parsedData.name = parsedData.Name;
        if (!parsedData.Name && parsedData.name) parsedData.Name = parsedData.name;
        
        console.log('✅ [auth.ts] user restaurado correctamente:', { 
            id: parsedData.id || parsedData.Id,
            email: parsedData.email || parsedData.Email,
            role: parsedData.role || parsedData.Role,
            name: parsedData.name || parsedData.Name
        });
        
        return parsedData;
    } catch (error) {
        console.error('❌ [auth.ts] Error al parsear user:', error);
        // Error al parsear datos de usuario - limpiar ambas claves
        localStorage.removeItem('user');
        localStorage.removeItem('userData');
        return null;
    }
}

export function updateUserData(updates: Partial<any>) {
    try {
        const userData = getUserData();
        if (!userData) {
            throw new Error('No user data found');
        }

        const updatedUserData = { ...userData, ...updates };
        localStorage.setItem('userData', JSON.stringify(updatedUserData));
        console.log('Updated user data:', updatedUserData);
        return updatedUserData;
    } catch (error) {
        console.error('Error updating user data:', error);
        throw error;
    }
}
```

#### 3. `src/contexts/AuthContext.tsx`
```typescript
import { createContext, useContext, useState, useEffect, ReactNode } from 'react';
import { User } from '../types/auth';
import { getAuthToken, getUserData, removeAuthToken, setAuthToken } from '../lib/auth';
import { authService } from '../services/authService';

interface AuthContextType {
    user: User | null;
    setUser: (user: User | null | ((prevUser: User | null) => User | null)) => void;
    isAuthenticated: boolean;
    isLoading: boolean;
    signOut: () => void;
    updateUser: (newUser: User | null, newToken: string | null, callback?: () => void) => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<User | null>(null);
    const [isAuthenticated, setIsAuthenticated] = useState<boolean>(false);
    const [isLoading, setIsLoading] = useState<boolean>(true);

    useEffect(() => {
        const restoreSession = async () => {
            setIsLoading(true);
            try {
                // ✅ CRÍTICO: Inicializar authService desde localStorage primero
                // Esto asegura que los tokens se carguen y se renueven si es necesario
                authService.initFromStorage();

                // Verificar tokens usando authService (más confiable)
                const accessToken = authService.getAccessToken();
                const refreshToken = authService.getRefreshToken();

                if (!accessToken || !refreshToken) {
                    console.log('⚠️ [AuthContext] No hay tokens en localStorage');
                    setUser(null);
                    setIsAuthenticated(false);
                    setIsLoading(false);
                    return;
                }

                // ✅ Verificar si el access token expiró usando accessTokenExpiresAt
                const expiresAt = localStorage.getItem('accessTokenExpiresAt');
                if (expiresAt) {
                    const expirationTime = new Date(expiresAt).getTime();
                    const now = Date.now();
                    if (expirationTime < now) {
                        console.log('⚠️ [AuthContext] Access token expirado, renovando...');
                        // Token expirado, intentar renovar
                        try {
                            const renewed = await authService.refreshAccessToken();
                            if (!renewed) {
                                console.log('❌ [AuthContext] No se pudo renovar el token - Refresh token expirado o inválido');
                                setUser(null);
                                setIsAuthenticated(false);
                                setIsLoading(false);
                                return;
                            }
                            console.log('✅ [AuthContext] Token renovado exitosamente');
                        } catch (error) {
                            console.error('❌ [AuthContext] Error al renovar token:', error);
                            setUser(null);
                            setIsAuthenticated(false);
                            setIsLoading(false);
                            return;
                        }
                    } else {
                        // Token válido, pero verificar si expira pronto (5 minutos)
                        const timeUntilExpiry = expirationTime - now;
                        if (timeUntilExpiry < 5 * 60 * 1000 && timeUntilExpiry > 0) {
                            console.log('🔄 [AuthContext] Token expira pronto, renovando proactivamente...');
                            // Renovar proactivamente (no bloqueante)
                            authService.refreshAccessToken().catch(err => {
                                console.warn('⚠️ [AuthContext] Error en renovación proactiva:', err);
                            });
                        }
                    }
                }

                // Obtener datos del usuario desde localStorage
                const storedUserData = getUserData();
                if (!storedUserData) {
                    console.log('⚠️ [AuthContext] No hay userData en localStorage');
                    // Token presente pero sin datos de usuario - limpiar silenciosamente
                    setUser(null);
                    setIsAuthenticated(false);
                    removeAuthToken();
                    setIsLoading(false);
                    return;
                }

                console.log('✅ [AuthContext] Sesión restaurada correctamente:', {
                    hasToken: !!accessToken,
                    hasRefreshToken: !!refreshToken,
                    userEmail: storedUserData.Email || storedUserData.email,
                    expiresAt: expiresAt
                });

                setUser(storedUserData);
                setIsAuthenticated(true);
            } catch (error: any) {
                console.error('❌ [AuthContext] Error al restaurar sesión:', error);
                // Error al restaurar sesión - limpiar y continuar
                setUser(null);
                setIsAuthenticated(false);
                removeAuthToken();
            } finally {
                setIsLoading(false);
            }
        };

        restoreSession();
    }, []);

    useEffect(() => {
        // Verificar autenticación basada en token y usuario
        const token = getAuthToken();
        const hasUser = !!user;
        const hasToken = !!token;
        const authenticated = hasUser && hasToken;
        
        // ✅ Solo actualizar si el estado realmente cambió para evitar re-renderizados innecesarios
        setIsAuthenticated(prev => {
            if (prev !== authenticated) {
                console.log('Auth state updated:', { user: user?.email, isAuthenticated: authenticated, hasToken });
                return authenticated;
            }
            return prev;
        });
    }, [user]);

    const signOut = async () => {
        console.log('Signing out user');
        await authService.logout();
        setUser(null);
        setIsAuthenticated(false);
        removeAuthToken();
    };

    const updateUser = (newUser: User | null, newToken: string | null, callback?: () => void) => {
        console.log('Updating user:', newUser, 'Token:', newToken ? 'present' : 'missing');
        setUser(newUser);
        if (newToken && newUser) {
            // ✅ CRÍTICO: Pasar tanto el token como el usuario para que se guarden ambos
            setAuthToken(newToken, newUser);
            setIsAuthenticated(true);
            console.log('✅ [AuthContext] Usuario y token guardados en localStorage');
        } else {
            removeAuthToken();
            setIsAuthenticated(false);
        }
        if (callback) {
            console.log('Executing updateUser callback');
            callback();
        }
    };

    return (
        <AuthContext.Provider value={{ user, setUser, isAuthenticated, isLoading, signOut, updateUser }}>
            {children}
        </AuthContext.Provider>
    );
}

export function useAuth() {
    const context = useContext(AuthContext);
    if (context === undefined) {
        throw new Error('useAuth must be used within an AuthProvider');
    }
    return context;
}

export const useAuthContext = useAuth;
```

---

### **BACKEND - NewApi**

#### 4. `Controllers/AuthController.cs`
(Véase el archivo completo en el código anterior - líneas 1-430)

**Endpoints principales:**
- `POST /api/Auth/refresh-token` - Renovar access token
- `POST /api/Auth/logout` - Cerrar sesión
- `POST /api/Auth/revoke-all` - Revocar todos los tokens
- `GET /api/Auth/mfa/status` - Estado de MFA

#### 5. `Services/UserService.cs`
(Véase el archivo completo - método `GoogleAuth` alrededor de línea 200+)

**Método principal:**
- `GoogleAuth(GoogleAuthDto request)` - Autenticación con Google OAuth

---

## 🔧 SOLUCIONES PROPUESTAS

### **Solución 1: Unificar Sistema de Tokens**

**Problema**: Hay dos sistemas (`authService.ts` y `lib/auth.ts`) que pueden entrar en conflicto.

**Solución**: Hacer que `lib/auth.ts` use `authService.ts` internamente:

```typescript
// lib/auth.ts - MODIFICADO
export function getAuthToken(): string | null {
    // ✅ Usar authService en lugar de acceder directamente a localStorage
    const token = authService.getAccessToken();
    if (!token) return null;

    try {
        const decoded: any = jwtDecode(token);
        const expirationTime = decoded.exp * 1000;
        const now = Date.now();
        
        if (expirationTime < now) {
            // ✅ NO limpiar - intentar renovar primero
            console.log('⚠️ [auth.ts] Token expirado, intentando renovar...');
            authService.refreshAccessToken().catch(err => {
                console.error('❌ [auth.ts] Error al renovar token:', err);
                // Solo limpiar si falla la renovación
                removeAuthToken();
            });
            // Devolver token actual mientras se renueva
            return token;
        }

        return token;
    } catch (error) {
        console.error('❌ [auth.ts] Error al parsear token:', error);
        // ✅ NO limpiar automáticamente - puede ser un error temporal
        return null;
    }
}
```

---

### **Solución 2: No Limpiar Tokens Automáticamente en `getUserData()`**

**Problema**: `getUserData()` limpia los datos si no encuentra `id` o `email`, pero estos pueden estar en el backend.

**Solución**: Intentar recuperar del backend antes de limpiar:

```typescript
// lib/auth.ts - MODIFICADO
export async function getUserData(): Promise<any | null> {
    try {
        let userData = localStorage.getItem('user') || localStorage.getItem('userData');
        if (!userData) {
            // ✅ Intentar recuperar del backend si hay token
            const token = authService.getAccessToken();
            if (token) {
                try {
                    const response = await fetch(`${API_CONFIG.baseUrl}/api/User/me`, {
                        headers: {
                            'Authorization': `Bearer ${token}`
                        }
                    });
                    if (response.ok) {
                        const user = await response.json();
                        setAuthToken(token, user);
                        return user;
                    }
                } catch (error) {
                    console.error('Error recuperando usuario del backend:', error);
                }
            }
            return null;
        }

        const parsedData = JSON.parse(userData);
        const userId = parsedData?.Id || parsedData?.id;
        const hasId = userId && !isNaN(userId);
        const hasEmail = parsedData?.Email || parsedData?.email;
        
        if (!parsedData || !hasId || !hasEmail) {
            // ✅ Intentar recuperar del backend antes de limpiar
            const token = authService.getAccessToken();
            if (token) {
                try {
                    const response = await fetch(`${API_CONFIG.baseUrl}/api/User/me`, {
                        headers: {
                            'Authorization': `Bearer ${token}`
                        }
                    });
                    if (response.ok) {
                        const user = await response.json();
                        setAuthToken(token, user);
                        return user;
                    }
                } catch (error) {
                    console.error('Error recuperando usuario del backend:', error);
                }
            }
            // Solo limpiar si no se pudo recuperar del backend
            localStorage.removeItem('user');
            localStorage.removeItem('userData');
            return null;
        }
        
        // ... resto del código igual
    } catch (error) {
        console.error('❌ [auth.ts] Error al parsear user:', error);
        // ✅ NO limpiar automáticamente - puede ser un error temporal
        return null;
    }
}
```

---

### **Solución 3: Mejorar Manejo de Refresh Token Expirado**

**Problema**: Cuando el refresh token expira (después de 7 días), se limpia todo sin dar opción al usuario.

**Solución**: Mostrar mensaje al usuario y dar opción de renovar sesión:

```typescript
// authService.ts - MODIFICADO
async refreshAccessToken(): Promise<boolean> {
    // ... código existente ...
    
    if (!response.ok) {
        if (response.status === 401) {
            console.warn('[AuthService] Refresh token inválido o expirado');
            
            // ✅ Disparar evento para que el componente maneje el error
            const expiredEvent = new CustomEvent('refreshTokenExpired', {
                detail: {
                    message: 'Tu sesión ha expirado. Por favor, inicia sesión de nuevo.',
                    canRenew: false
                }
            });
            window.dispatchEvent(expiredEvent);
            
            // ✅ NO limpiar inmediatamente - dejar que el componente decida
            // this.clearTokens(); // COMENTADO
            return false;
        }
        throw new Error('Failed to refresh token');
    }
    
    // ... resto del código igual
}
```

---

### **Solución 4: Sincronizar AuthContext con authService**

**Problema**: `AuthContext` usa `getAuthToken()` de `lib/auth.ts` que puede limpiar tokens que `authService` considera válidos.

**Solución**: Hacer que `AuthContext` use directamente `authService`:

```typescript
// AuthContext.tsx - MODIFICADO
useEffect(() => {
    // Verificar autenticación basada en authService
    const isAuth = authService.isAuthenticated();
    const currentUser = authService.getCurrentUser();
    
    // ✅ Sincronizar con authService
    setIsAuthenticated(isAuth);
    if (isAuth && currentUser) {
        // Obtener datos completos del usuario
        const userData = getUserData();
        if (userData) {
            setUser(userData);
        } else if (currentUser) {
            // Si no hay userData pero hay token válido, usar datos del token
            setUser({
                id: currentUser.sub || currentUser.id,
                email: currentUser.email,
                name: currentUser.name,
                // ... otros campos
            });
        }
    } else {
        setUser(null);
    }
}, []);
```

---

## 📝 CHECKLIST DE VERIFICACIÓN

Para diagnosticar el problema, verificar:

- [ ] ¿Se limpian los tokens en `getAuthToken()` cuando el token expira?
- [ ] ¿Se limpian los datos de usuario en `getUserData()` cuando falta `id` o `email`?
- [ ] ¿El refresh token expira antes de lo esperado?
- [ ] ¿Hay errores en la consola del navegador relacionados con tokens?
- [ ] ¿Se llama `removeAuthToken()` en algún lugar inesperado?
- [ ] ¿Hay conflictos entre `authService` y `lib/auth.ts`?

---

## 🎯 PREGUNTAS PARA LA IA PAGADA

1. **¿Por qué se limpian los tokens automáticamente cuando el usuario no ha cerrado sesión?**
   - Revisar `getAuthToken()` en `lib/auth.ts` - líneas 47-68
   - Revisar `getUserData()` en `lib/auth.ts` - líneas 94-108
   - Revisar `refreshAccessToken()` en `authService.ts` - líneas 193-199

2. **¿Cómo unificar los dos sistemas de tokens (`authService.ts` y `lib/auth.ts`)?**
   - `authService.ts` usa `accessToken` y `refreshToken`
   - `lib/auth.ts` usa `authToken`
   - Pueden entrar en conflicto

3. **¿Cómo mejorar el manejo de tokens expirados sin limpiar la sesión automáticamente?**
   - Actualmente se limpia todo cuando el token expira
   - Debería intentar renovar primero

4. **¿Cómo sincronizar `AuthContext` con `authService` para evitar inconsistencias?**
   - `AuthContext` usa `getAuthToken()` de `lib/auth.ts`
   - `authService` tiene su propio sistema
   - Pueden desincronizarse

---

## 📊 FLUJO ACTUAL (PROBLEMÁTICO)

```
Usuario entra a la app
    ↓
AuthContext.restoreSession()
    ↓
authService.initFromStorage() ✅
    ↓
getAuthToken() de lib/auth.ts
    ↓
¿Token válido? → NO → removeAuthToken() ❌ (LIMPIA TODO)
    ↓
getUserData() de lib/auth.ts
    ↓
¿Datos válidos? → NO → removeAuthToken() ❌ (LIMPIA TODO)
    ↓
Usuario desregistrado automáticamente ❌
```

---

## 📊 FLUJO PROPUESTO (CORREGIDO)

```
Usuario entra a la app
    ↓
AuthContext.restoreSession()
    ↓
authService.initFromStorage() ✅
    ↓
authService.getAccessToken() ✅ (USA authService, no lib/auth.ts)
    ↓
¿Token válido? → NO → authService.refreshAccessToken() ✅ (INTENTA RENOVAR)
    ↓
¿Renovación exitosa? → SÍ → Continuar ✅
    ↓
¿Renovación exitosa? → NO → Mostrar mensaje al usuario (NO limpiar automáticamente)
    ↓
getUserData() mejorado (intenta recuperar del backend antes de limpiar)
    ↓
Usuario mantiene sesión ✅
```

---

**FIN DEL DOCUMENTO**
