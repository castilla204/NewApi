# 🚀 GUÍA COMPLETA FRONTEND - SEGURIDAD 2025

## 📋 ÍNDICE

1. [Setup Inicial](#1-setup-inicial)
2. [Manejo de Tokens (Refresh Tokens)](#2-manejo-de-tokens-refresh-tokens)
3. [Rate Limiting](#3-rate-limiting)
4. [MFA (Autenticación Multifactor)](#4-mfa-autenticación-multifactor)
5. [Flujos Completos](#5-flujos-completos)
6. [Testing](#6-testing)

---

## 1. SETUP INICIAL

### 1.1 Instalar Dependencias

```bash
# Si usas React
npm install axios jwt-decode

# Si usas Vue
npm install axios jwt-decode

# Si usas Angular
npm install jwt-decode
```

### 1.2 Configuración Base

**`config.js` o `.env`**

```javascript
// config.js
export const API_BASE_URL = 'http://localhost:7124/api';

export const ENDPOINTS = {
  // Auth
  GOOGLE_AUTH: '/user/google-auth',
  REFRESH_TOKEN: '/auth/refresh-token',
  LOGOUT: '/auth/logout',
  REVOKE_ALL: '/auth/revoke-all',
  
  // MFA
  MFA_SETUP: '/auth/mfa/setup',
  MFA_ENABLE: '/auth/mfa/enable',
  MFA_VERIFY: '/auth/mfa/verify',
  MFA_DISABLE: '/auth/mfa/disable',
  MFA_STATUS: '/auth/mfa/status',
};
```

---

## 2. MANEJO DE TOKENS (REFRESH TOKENS)

### 2.1 Servicio de Autenticación (`authService.js`)

```javascript
// authService.js
import axios from 'axios';
import jwtDecode from 'jwt-decode';
import { API_BASE_URL, ENDPOINTS } from './config';

class AuthService {
  constructor() {
    this.accessToken = null;
    this.refreshToken = null;
    this.refreshTimeout = null;
  }

  // ============================================
  // 1. GOOGLE AUTH (Login)
  // ============================================
  async googleAuth(googleCredential) {
    try {
      // Decodificar el credential de Google
      const decoded = jwtDecode(googleCredential);
      
      const response = await axios.post(`${API_BASE_URL}${ENDPOINTS.GOOGLE_AUTH}`, {
        accessToken: googleCredential,
        email: decoded.email,
        name: decoded.name,
        googleId: decoded.sub
      });

      if (response.data.token) {
        // ✅ CRÍTICO: El backend devuelve "accessToken|refreshToken"
        const [accessToken, refreshToken] = response.data.token.split('|');
        
        this.setTokens(accessToken, refreshToken);
        
        // Iniciar auto-renovación
        this.scheduleTokenRefresh();
        
        return {
          success: true,
          user: response.data.user,
          requiresMFA: response.data.requiresMFA || false
        };
      }
    } catch (error) {
      console.error('Google Auth error:', error);
      throw error;
    }
  }

  // ============================================
  // 2. GUARDAR TOKENS
  // ============================================
  setTokens(accessToken, refreshToken) {
    this.accessToken = accessToken;
    this.refreshToken = refreshToken;
    
    // Guardar en localStorage (o sessionStorage si prefieres)
    localStorage.setItem('accessToken', accessToken);
    localStorage.setItem('refreshToken', refreshToken);
    
    // Configurar axios para usar el access token
    this.setAuthHeader(accessToken);
  }

  setAuthHeader(token) {
    if (token) {
      axios.defaults.headers.common['Authorization'] = `Bearer ${token}`;
    } else {
      delete axios.defaults.headers.common['Authorization'];
    }
  }

  // ============================================
  // 3. RENOVAR ACCESS TOKEN AUTOMÁTICAMENTE
  // ============================================
  scheduleTokenRefresh() {
    // Limpiar timeout anterior
    if (this.refreshTimeout) {
      clearTimeout(this.refreshTimeout);
    }

    try {
      const decoded = jwtDecode(this.accessToken);
      const expiresIn = decoded.exp * 1000 - Date.now();
      
      // Renovar 2 minutos antes de expirar (Access Token dura 30 min)
      const refreshIn = expiresIn - (2 * 60 * 1000);
      
      console.log(`Token expira en ${Math.floor(expiresIn / 1000 / 60)} minutos. Renovando en ${Math.floor(refreshIn / 1000 / 60)} minutos.`);

      if (refreshIn > 0) {
        this.refreshTimeout = setTimeout(() => {
          this.refreshAccessToken();
        }, refreshIn);
      } else {
        // Token ya expiró, renovar inmediatamente
        this.refreshAccessToken();
      }
    } catch (error) {
      console.error('Error scheduling token refresh:', error);
    }
  }

  async refreshAccessToken() {
    try {
      const response = await axios.post(
        `${API_BASE_URL}${ENDPOINTS.REFRESH_TOKEN}`,
        { refreshToken: this.refreshToken }
      );

      if (response.data.accessToken && response.data.refreshToken) {
        // ✅ Backend devuelve NUEVOS access y refresh tokens (rotación)
        this.setTokens(response.data.accessToken, response.data.refreshToken);
        
        // Programar próxima renovación
        this.scheduleTokenRefresh();
        
        console.log('✅ Token renovado exitosamente');
        return true;
      }
    } catch (error) {
      console.error('Error refreshing token:', error);
      
      if (error.response?.status === 401) {
        // Refresh token inválido o expirado → Logout
        this.logout();
        window.location.href = '/login';
      }
      
      return false;
    }
  }

  // ============================================
  // 4. INTERCEPTOR AXIOS (Auto-renovación en 401)
  // ============================================
  setupAxiosInterceptor() {
    axios.interceptors.response.use(
      (response) => response,
      async (error) => {
        const originalRequest = error.config;

        // Si recibimos 401 y no hemos intentado renovar ya
        if (error.response?.status === 401 && !originalRequest._retry) {
          originalRequest._retry = true;

          const success = await this.refreshAccessToken();
          
          if (success) {
            // Reintentar request original con nuevo token
            originalRequest.headers['Authorization'] = `Bearer ${this.accessToken}`;
            return axios(originalRequest);
          }
        }

        return Promise.reject(error);
      }
    );
  }

  // ============================================
  // 5. LOGOUT
  // ============================================
  async logout() {
    try {
      // Revocar refresh token en el servidor
      if (this.refreshToken) {
        await axios.post(`${API_BASE_URL}${ENDPOINTS.LOGOUT}`, {
          refreshToken: this.refreshToken
        });
      }
    } catch (error) {
      console.error('Logout error:', error);
    } finally {
      // Limpiar todo
      this.clearTokens();
    }
  }

  async logoutAllDevices() {
    try {
      await axios.post(`${API_BASE_URL}${ENDPOINTS.REVOKE_ALL}`);
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
    
    delete axios.defaults.headers.common['Authorization'];
    
    if (this.refreshTimeout) {
      clearTimeout(this.refreshTimeout);
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
      this.setAuthHeader(accessToken);
      
      // Verificar si el access token ya expiró
      try {
        const decoded = jwtDecode(accessToken);
        const isExpired = decoded.exp * 1000 < Date.now();
        
        if (isExpired) {
          // Renovar inmediatamente
          this.refreshAccessToken();
        } else {
          // Programar renovación
          this.scheduleTokenRefresh();
        }
      } catch (error) {
        console.error('Invalid token:', error);
        this.clearTokens();
      }
    }
  }

  // ============================================
  // 7. HELPERS
  // ============================================
  isAuthenticated() {
    return !!this.accessToken && !!this.refreshToken;
  }

  getAccessToken() {
    return this.accessToken;
  }

  getCurrentUser() {
    if (!this.accessToken) return null;
    
    try {
      return jwtDecode(this.accessToken);
    } catch (error) {
      return null;
    }
  }
}

// Exportar instancia singleton
export const authService = new AuthService();
```

---

## 3. RATE LIMITING

### 3.1 Interceptor para Manejo de 429

```javascript
// rateLimitHandler.js
import axios from 'axios';

export function setupRateLimitHandler() {
  axios.interceptors.response.use(
    (response) => response,
    (error) => {
      if (error.response?.status === 429) {
        const retryAfter = error.response.data?.retryAfter || 60;
        
        // Mostrar notificación al usuario
        showRateLimitNotification(retryAfter);
        
        // Opcional: Reintentar automáticamente después del tiempo
        if (error.config.autoRetry !== false) {
          return new Promise((resolve) => {
            setTimeout(() => {
              resolve(axios(error.config));
            }, retryAfter * 1000);
          });
        }
      }
      
      return Promise.reject(error);
    }
  );
}

function showRateLimitNotification(seconds) {
  // Implementar según tu librería de notificaciones
  // Ejemplos:
  
  // Toast (react-hot-toast)
  // toast.error(`Too many requests. Please wait ${seconds} seconds.`);
  
  // Alert
  alert(`Demasiadas solicitudes. Por favor espera ${seconds} segundos.`);
  
  // Notification API
  if ('Notification' in window && Notification.permission === 'granted') {
    new Notification('Límite de solicitudes alcanzado', {
      body: `Espera ${seconds} segundos antes de intentar de nuevo.`,
      icon: '/warning-icon.png'
    });
  }
}
```

### 3.2 Deshabilitar Botones Durante Rate Limit

```javascript
// useRateLimit.js (React Hook)
import { useState, useCallback } from 'react';
import axios from 'axios';

export function useRateLimit() {
  const [isRateLimited, setIsRateLimited] = useState(false);
  const [retryAfter, setRetryAfter] = useState(0);

  const makeRequest = useCallback(async (requestFn) => {
    if (isRateLimited) {
      throw new Error(`Rate limited. Retry after ${retryAfter} seconds.`);
    }

    try {
      return await requestFn();
    } catch (error) {
      if (error.response?.status === 429) {
        const retrySeconds = error.response.data?.retryAfter || 60;
        setIsRateLimited(true);
        setRetryAfter(retrySeconds);

        // Auto-resetear después del tiempo
        setTimeout(() => {
          setIsRateLimited(false);
          setRetryAfter(0);
        }, retrySeconds * 1000);
      }
      throw error;
    }
  }, [isRateLimited, retryAfter]);

  return { makeRequest, isRateLimited, retryAfter };
}

// Uso:
// const { makeRequest, isRateLimited, retryAfter } = useRateLimit();
// 
// const handleLogin = async () => {
//   await makeRequest(() => authService.googleAuth(credential));
// };
// 
// <button disabled={isRateLimited}>
//   {isRateLimited ? `Wait ${retryAfter}s` : 'Login'}
// </button>
```

---

## 4. MFA (AUTENTICACIÓN MULTIFACTOR)

### 4.1 Servicio MFA (`mfaService.js`)

```javascript
// mfaService.js
import axios from 'axios';
import { API_BASE_URL, ENDPOINTS } from './config';

class MFAService {
  // ============================================
  // 1. SETUP MFA (Obtener QR code)
  // ============================================
  async setupMFA() {
    try {
      const response = await axios.post(`${API_BASE_URL}${ENDPOINTS.MFA_SETUP}`);
      return {
        qrCodeBase64: response.data.qrCodeBase64,
        manualEntryKey: response.data.manualEntryKey,
        message: response.data.message
      };
    } catch (error) {
      console.error('MFA Setup error:', error);
      throw error;
    }
  }

  // ============================================
  // 2. ENABLE MFA (Confirmar con código)
  // ============================================
  async enableMFA(totpCode) {
    try {
      const response = await axios.post(`${API_BASE_URL}${ENDPOINTS.MFA_ENABLE}`, {
        totpCode
      });
      
      return {
        success: response.data.success,
        recoveryCodes: response.data.recoveryCodes,
        message: response.data.message
      };
    } catch (error) {
      console.error('MFA Enable error:', error);
      throw error;
    }
  }

  // ============================================
  // 3. VERIFY MFA (Durante login)
  // ============================================
  async verifyMFA(code, isRecoveryCode = false) {
    try {
      const response = await axios.post(`${API_BASE_URL}${ENDPOINTS.MFA_VERIFY}`, {
        code,
        isRecoveryCode
      });
      
      if (response.data.accessToken && response.data.refreshToken) {
        return {
          isValid: true,
          accessToken: response.data.accessToken,
          refreshToken: response.data.refreshToken,
          message: response.data.message
        };
      }
      
      return { isValid: false };
    } catch (error) {
      console.error('MFA Verify error:', error);
      throw error;
    }
  }

  // ============================================
  // 4. DISABLE MFA
  // ============================================
  async disableMFA(password, totpCode) {
    try {
      const response = await axios.post(`${API_BASE_URL}${ENDPOINTS.MFA_DISABLE}`, {
        password,
        totpCode
      });
      
      return {
        success: true,
        message: response.data.message
      };
    } catch (error) {
      console.error('MFA Disable error:', error);
      throw error;
    }
  }

  // ============================================
  // 5. GET MFA STATUS
  // ============================================
  async getMFAStatus() {
    try {
      const response = await axios.get(`${API_BASE_URL}${ENDPOINTS.MFA_STATUS}`);
      return response.data;
    } catch (error) {
      console.error('MFA Status error:', error);
      throw error;
    }
  }
}

export const mfaService = new MFAService();
```

### 4.2 Componente de Setup MFA (React)

```jsx
// MFASetup.jsx
import React, { useState } from 'react';
import { mfaService } from './mfaService';
import QRCode from 'qrcode.react'; // npm install qrcode.react

export function MFASetup({ onComplete }) {
  const [step, setStep] = useState(1); // 1: Setup, 2: Verify, 3: Recovery Codes
  const [qrCode, setQrCode] = useState('');
  const [manualKey, setManualKey] = useState('');
  const [totpCode, setTotpCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  // ============================================
  // PASO 1: Obtener QR Code
  // ============================================
  const handleStartSetup = async () => {
    setLoading(true);
    setError('');
    
    try {
      const result = await mfaService.setupMFA();
      setQrCode(result.qrCodeBase64);
      setManualKey(result.manualEntryKey);
      setStep(2);
    } catch (err) {
      setError(err.response?.data?.message || 'Error al configurar MFA');
    } finally {
      setLoading(false);
    }
  };

  // ============================================
  // PASO 2: Verificar código y habilitar MFA
  // ============================================
  const handleEnableMFA = async (e) => {
    e.preventDefault();
    setLoading(true);
    setError('');
    
    try {
      const result = await mfaService.enableMFA(totpCode);
      setRecoveryCodes(result.recoveryCodes);
      setStep(3);
    } catch (err) {
      setError(err.response?.data?.message || 'Código inválido');
    } finally {
      setLoading(false);
    }
  };

  // ============================================
  // PASO 3: Descargar códigos de recuperación
  // ============================================
  const downloadRecoveryCodes = () => {
    const blob = new Blob([recoveryCodes.join('\n')], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'mfa-recovery-codes.txt';
    a.click();
    URL.revokeObjectURL(url);
  };

  const copyRecoveryCodes = () => {
    navigator.clipboard.writeText(recoveryCodes.join('\n'));
    alert('Códigos copiados al portapapeles');
  };

  // ============================================
  // RENDER
  // ============================================
  return (
    <div className="mfa-setup">
      {/* PASO 1: Iniciar */}
      {step === 1 && (
        <div className="step-1">
          <h2>Configurar Autenticación de Dos Factores</h2>
          <p>Aumenta la seguridad de tu cuenta con MFA.</p>
          <button onClick={handleStartSetup} disabled={loading}>
            {loading ? 'Cargando...' : 'Comenzar Configuración'}
          </button>
        </div>
      )}

      {/* PASO 2: Escanear QR y verificar */}
      {step === 2 && (
        <div className="step-2">
          <h2>Escanea el código QR</h2>
          
          {/* QR Code */}
          <div className="qr-code">
            <img 
              src={`data:image/png;base64,${qrCode}`} 
              alt="MFA QR Code"
              style={{ width: '300px', height: '300px' }}
            />
          </div>

          {/* Clave manual (por si no puede escanear) */}
          <details>
            <summary>¿No puedes escanear el código?</summary>
            <p>Ingresa esta clave manualmente en tu app:</p>
            <code style={{ 
              display: 'block', 
              padding: '10px', 
              background: '#f5f5f5',
              fontSize: '16px',
              fontFamily: 'monospace'
            }}>
              {manualKey}
            </code>
            <button onClick={() => navigator.clipboard.writeText(manualKey)}>
              Copiar clave
            </button>
          </details>

          {/* Instrucciones */}
          <div className="instructions">
            <h3>Instrucciones:</h3>
            <ol>
              <li>Abre tu app de autenticación (Google Authenticator, Microsoft Authenticator, etc.)</li>
              <li>Escanea el código QR o ingresa la clave manual</li>
              <li>Ingresa el código de 6 dígitos que aparece en tu app</li>
            </ol>
          </div>

          {/* Formulario de verificación */}
          <form onSubmit={handleEnableMFA}>
            <label>
              Código de 6 dígitos:
              <input
                type="text"
                value={totpCode}
                onChange={(e) => setTotpCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
                placeholder="123456"
                maxLength="6"
                required
                style={{
                  fontSize: '24px',
                  textAlign: 'center',
                  letterSpacing: '5px',
                  padding: '10px'
                }}
              />
            </label>

            {error && <p className="error">{error}</p>}

            <button type="submit" disabled={loading || totpCode.length !== 6}>
              {loading ? 'Verificando...' : 'Habilitar MFA'}
            </button>
          </form>
        </div>
      )}

      {/* PASO 3: Códigos de recuperación */}
      {step === 3 && (
        <div className="step-3">
          <h2>⚠️ Guarda tus códigos de recuperación</h2>
          <p style={{ color: 'red', fontWeight: 'bold' }}>
            IMPORTANTE: Guarda estos códigos en un lugar seguro. 
            Solo se mostrarán una vez y los necesitarás si pierdes acceso a tu app de autenticación.
          </p>

          {/* Códigos de recuperación */}
          <div className="recovery-codes" style={{
            background: '#f5f5f5',
            padding: '20px',
            borderRadius: '8px',
            marginTop: '20px'
          }}>
            {recoveryCodes.map((code, index) => (
              <div key={index} style={{
                fontFamily: 'monospace',
                fontSize: '18px',
                padding: '5px',
                borderBottom: '1px solid #ddd'
              }}>
                {index + 1}. {code}
              </div>
            ))}
          </div>

          {/* Acciones */}
          <div className="actions" style={{ marginTop: '20px' }}>
            <button onClick={downloadRecoveryCodes} style={{ marginRight: '10px' }}>
              📥 Descargar códigos
            </button>
            <button onClick={copyRecoveryCodes}>
              📋 Copiar códigos
            </button>
          </div>

          {/* Checkbox de confirmación */}
          <div style={{ marginTop: '30px' }}>
            <label>
              <input type="checkbox" required />
              {' '}Confirmo que he guardado mis códigos de recuperación de forma segura
            </label>
          </div>

          <button 
            onClick={onComplete}
            style={{ marginTop: '20px', width: '100%' }}
          >
            ✅ Completar configuración
          </button>
        </div>
      )}
    </div>
  );
}
```

### 4.3 Componente de Verificación MFA (Durante Login)

```jsx
// MFAVerify.jsx
import React, { useState } from 'react';
import { mfaService } from './mfaService';
import { authService } from './authService';

export function MFAVerify({ onSuccess, onCancel }) {
  const [code, setCode] = useState('');
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const handleSubmit = async (e) => {
    e.preventDefault();
    setLoading(true);
    setError('');

    try {
      const result = await mfaService.verifyMFA(code, useRecoveryCode);
      
      if (result.isValid) {
        // Guardar nuevos tokens
        authService.setTokens(result.accessToken, result.refreshToken);
        authService.scheduleTokenRefresh();
        
        // Mostrar mensaje si quedan pocos recovery codes
        if (result.message) {
          alert(result.message);
        }
        
        onSuccess();
      } else {
        setError('Código inválido');
      }
    } catch (err) {
      setError(err.response?.data?.message || 'Error al verificar código');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="mfa-verify">
      <h2>Verificación de Dos Factores</h2>
      <p>Ingresa el código de tu app de autenticación</p>

      <form onSubmit={handleSubmit}>
        <label>
          {useRecoveryCode ? 'Código de recuperación:' : 'Código de 6 dígitos:'}
          <input
            type="text"
            value={code}
            onChange={(e) => {
              const value = e.target.value.toUpperCase();
              setCode(useRecoveryCode ? value : value.replace(/\D/g, '').slice(0, 6));
            }}
            placeholder={useRecoveryCode ? 'XXXX-XXXX' : '123456'}
            maxLength={useRecoveryCode ? 9 : 6}
            required
            style={{
              fontSize: '24px',
              textAlign: 'center',
              letterSpacing: useRecoveryCode ? '2px' : '5px',
              padding: '15px',
              width: '100%'
            }}
          />
        </label>

        {error && <p className="error" style={{ color: 'red' }}>{error}</p>}

        <button 
          type="submit" 
          disabled={loading || (!useRecoveryCode && code.length !== 6) || (useRecoveryCode && code.length < 8)}
        >
          {loading ? 'Verificando...' : 'Verificar'}
        </button>
      </form>

      {/* Toggle recovery code */}
      <button 
        onClick={() => {
          setUseRecoveryCode(!useRecoveryCode);
          setCode('');
          setError('');
        }}
        style={{ marginTop: '20px', background: 'transparent', border: 'none', color: '#0066cc', cursor: 'pointer' }}
      >
        {useRecoveryCode ? '← Usar código de la app' : 'Usar código de recuperación →'}
      </button>

      {/* Cancelar */}
      <button 
        onClick={onCancel}
        style={{ marginTop: '10px', background: '#ccc' }}
      >
        Cancelar
      </button>
    </div>
  );
}
```

---

## 5. FLUJOS COMPLETOS

### 5.1 Flujo de Login con Google Auth

```jsx
// LoginPage.jsx
import React, { useState } from 'react';
import { GoogleLogin } from '@react-oauth/google'; // npm install @react-oauth/google
import { authService } from './authService';
import { MFAVerify } from './MFAVerify';

export function LoginPage() {
  const [requiresMFA, setRequiresMFA] = useState(false);
  const [tempToken, setTempToken] = useState('');
  const [loading, setLoading] = useState(false);

  const handleGoogleSuccess = async (credentialResponse) => {
    setLoading(true);

    try {
      const result = await authService.googleAuth(credentialResponse.credential);

      if (result.requiresMFA) {
        // Usuario tiene MFA habilitado → Mostrar pantalla de verificación
        setRequiresMFA(true);
        // El token ya está guardado, solo necesitamos verificar MFA
      } else {
        // Login exitoso sin MFA
        window.location.href = '/dashboard';
      }
    } catch (error) {
      alert('Error al iniciar sesión: ' + (error.response?.data?.message || error.message));
    } finally {
      setLoading(false);
    }
  };

  const handleMFASuccess = () => {
    // MFA verificado → Redirigir
    window.location.href = '/dashboard';
  };

  const handleMFACancel = () => {
    // Cancelar MFA → Logout y volver al login
    authService.logout();
    setRequiresMFA(false);
  };

  if (requiresMFA) {
    return <MFAVerify onSuccess={handleMFASuccess} onCancel={handleMFACancel} />;
  }

  return (
    <div className="login-page">
      <h1>Iniciar Sesión</h1>
      
      <GoogleLogin
        onSuccess={handleGoogleSuccess}
        onError={() => alert('Error al iniciar sesión con Google')}
        useOneTap
      />

      {loading && <p>Cargando...</p>}
    </div>
  );
}
```

### 5.2 Flujo Completo de Configuración MFA

```jsx
// SettingsPage.jsx
import React, { useState, useEffect } from 'react';
import { mfaService } from './mfaService';
import { MFASetup } from './MFASetup';

export function SettingsPage() {
  const [mfaStatus, setMfaStatus] = useState(null);
  const [showSetup, setShowSetup] = useState(false);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    loadMFAStatus();
  }, []);

  const loadMFAStatus = async () => {
    try {
      const status = await mfaService.getMFAStatus();
      setMfaStatus(status);
    } catch (error) {
      console.error('Error loading MFA status:', error);
    } finally {
      setLoading(false);
    }
  };

  const handleMFASetupComplete = () => {
    setShowSetup(false);
    loadMFAStatus();
    alert('✅ MFA configurado exitosamente');
  };

  const handleDisableMFA = async () => {
    const password = prompt('Ingresa tu contraseña:');
    const totpCode = prompt('Ingresa el código de tu app:');

    if (!password || !totpCode) return;

    try {
      await mfaService.disableMFA(password, totpCode);
      loadMFAStatus();
      alert('MFA deshabilitado');
    } catch (error) {
      alert('Error al deshabilitar MFA: ' + (error.response?.data?.message || error.message));
    }
  };

  if (loading) return <p>Cargando...</p>;

  if (showSetup) {
    return <MFASetup onComplete={handleMFASetupComplete} />;
  }

  return (
    <div className="settings-page">
      <h1>Configuración de Seguridad</h1>

      {/* Estado de MFA */}
      <div className="mfa-section">
        <h2>Autenticación de Dos Factores (MFA)</h2>
        
        {mfaStatus?.isEnabled ? (
          <div className="mfa-enabled">
            <p>✅ MFA Habilitado</p>
            <p>Habilitado el: {new Date(mfaStatus.enabledAt).toLocaleDateString()}</p>
            <p>Última verificación: {new Date(mfaStatus.lastVerifiedAt).toLocaleDateString()}</p>
            <p>Códigos de recuperación restantes: {mfaStatus.remainingRecoveryCodes}</p>
            
            {mfaStatus.remainingRecoveryCodes <= 3 && (
              <p style={{ color: 'red', fontWeight: 'bold' }}>
                ⚠️ Quedan pocos códigos de recuperación. Considera regenerarlos.
              </p>
            )}
            
            <button onClick={handleDisableMFA} style={{ background: '#dc3545', color: 'white' }}>
              Deshabilitar MFA
            </button>
          </div>
        ) : (
          <div className="mfa-disabled">
            <p>❌ MFA No habilitado</p>
            <p>Recomendamos habilitar MFA para mayor seguridad.</p>
            <button onClick={() => setShowSetup(true)} style={{ background: '#28a745', color: 'white' }}>
              Habilitar MFA
            </button>
          </div>
        )}
      </div>

      {/* Otras opciones de seguridad */}
      <div className="security-section">
        <h2>Sesiones Activas</h2>
        <button onClick={() => authService.logoutAllDevices()}>
          Cerrar sesión en todos los dispositivos
        </button>
      </div>
    </div>
  );
}
```

### 5.3 Inicialización de la App

```jsx
// App.jsx (o index.jsx)
import React, { useEffect } from 'react';
import { authService } from './authService';
import { setupRateLimitHandler } from './rateLimitHandler';

function App() {
  useEffect(() => {
    // 1. Cargar tokens desde localStorage
    authService.initFromStorage();
    
    // 2. Configurar interceptor de axios
    authService.setupAxiosInterceptor();
    
    // 3. Configurar manejo de rate limiting
    setupRateLimitHandler();
  }, []);

  return (
    <div className="app">
      {/* Tu app aquí */}
    </div>
  );
}

export default App;
```

---

## 6. TESTING

### 6.1 Test Manual del Flujo Completo

```javascript
// test-flow.js
import { authService } from './authService';
import { mfaService } from './mfaService';

async function testCompleteFlow() {
  console.log('🧪 Testing Complete Security Flow...\n');

  try {
    // 1. Google Login
    console.log('1️⃣ Testing Google Login...');
    const googleCredential = 'YOUR_GOOGLE_JWT'; // Obtener de Google Sign-In
    const loginResult = await authService.googleAuth(googleCredential);
    console.log('✅ Login successful:', loginResult);
    
    // 2. Setup MFA
    console.log('\n2️⃣ Testing MFA Setup...');
    const setupResult = await mfaService.setupMFA();
    console.log('✅ MFA Setup:', setupResult);
    console.log('📱 Manual Key:', setupResult.manualEntryKey);
    
    // 3. Enable MFA (usar código de tu app)
    const totpCode = prompt('Enter TOTP code from your app:');
    const enableResult = await mfaService.enableMFA(totpCode);
    console.log('✅ MFA Enabled:', enableResult);
    console.log('🔑 Recovery Codes:', enableResult.recoveryCodes);
    
    // 4. Logout
    console.log('\n3️⃣ Testing Logout...');
    await authService.logout();
    console.log('✅ Logged out');
    
    // 5. Login con MFA
    console.log('\n4️⃣ Testing Login with MFA...');
    await authService.googleAuth(googleCredential);
    const mfaCode = prompt('Enter MFA code:');
    const verifyResult = await mfaService.verifyMFA(mfaCode, false);
    console.log('✅ MFA Verified:', verifyResult);
    
    // 6. Test Refresh Token
    console.log('\n5️⃣ Testing Refresh Token...');
    await new Promise(resolve => setTimeout(resolve, 2000)); // Esperar 2s
    const refreshed = await authService.refreshAccessToken();
    console.log('✅ Token Refreshed:', refreshed);
    
    // 7. MFA Status
    console.log('\n6️⃣ Testing MFA Status...');
    const status = await mfaService.getMFAStatus();
    console.log('✅ MFA Status:', status);
    
    console.log('\n🎉 All tests passed!');
  } catch (error) {
    console.error('❌ Test failed:', error);
  }
}

// Ejecutar tests
// testCompleteFlow();
```

### 6.2 Checklist de Testing

```markdown
## ✅ Testing Checklist

### Tokens
- [ ] Google Login genera y guarda tokens correctamente
- [ ] Access token expira en 30 minutos
- [ ] Refresh token funciona antes de expiración
- [ ] Auto-renovación funciona (esperar 28 minutos)
- [ ] 401 dispara renovación automática
- [ ] Logout revoca tokens en el servidor
- [ ] Tokens persisten después de refresh de página

### Rate Limiting
- [ ] 6 intentos de login → Bloqueo de 5 minutos
- [ ] Mensaje de "Too many requests" se muestra
- [ ] `retryAfter` se respeta
- [ ] Reintentos automáticos funcionan (si configurado)

### MFA
- [ ] QR code se genera correctamente
- [ ] Clave manual funciona
- [ ] Google Authenticator genera códigos válidos
- [ ] Códigos de 6 dígitos se verifican
- [ ] Recovery codes funcionan (probar 1)
- [ ] Recovery codes se eliminan después de usar
- [ ] Alerta de "quedan pocos códigos"
- [ ] 6 códigos incorrectos → Bloqueo de 15 minutos
- [ ] Deshabilitar MFA funciona
- [ ] Login con MFA habilitado solicita código

### Flujos
- [ ] Login sin MFA funciona
- [ ] Login con MFA funciona
- [ ] Configurar MFA después de login funciona
- [ ] Logout limpia todo correctamente
- [ ] Múltiples dispositivos funcionan
```

---

## 7. TROUBLESHOOTING

### Problema 1: "Token inválido" al recargar página

**Causa:** Access token expiró durante la sesión.

**Solución:**
```javascript
// Verificar en authService.initFromStorage():
const decoded = jwtDecode(accessToken);
const isExpired = decoded.exp * 1000 < Date.now();

if (isExpired) {
  this.refreshAccessToken(); // ← Renovar inmediatamente
}
```

### Problema 2: "Invalid TOTP code" en MFA

**Causas comunes:**
1. Reloj del servidor desfasado
2. Usuario escaneó QR incorrecto
3. Usuario tiene múltiples cuentas en la app

**Solución:**
- Verificar sincronización de reloj (NTP)
- Reintentar con nuevo QR
- Asegurar que el usuario usa el código correcto

### Problema 3: Rate Limiting se dispara rápidamente

**Causa:** IP compartida (NAT, empresa, etc.)

**Solución en backend:**
```csharp
// En Program.cs, ajustar límites:
opt.PermitLimit = 10; // Aumentar de 5 a 10
```

---

## 🎉 ¡LISTO!

Con esta guía tienes TODO lo necesario para implementar:

✅ **Refresh Tokens** con auto-renovación  
✅ **Rate Limiting** con manejo de errores  
✅ **MFA** completo (TOTP + Recovery Codes)  

**Tu aplicación frontend ahora es tan segura como el backend.** 🔐

---

## 📞 SOPORTE

Si encuentras problemas:

1. Verifica la consola del navegador
2. Verifica la consola del servidor
3. Usa las herramientas de desarrollo de Chrome/Firefox
4. Prueba los endpoints con Postman primero

**¡Buena suerte!** 🚀

