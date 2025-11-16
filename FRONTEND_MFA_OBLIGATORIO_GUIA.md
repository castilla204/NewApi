# 🔐 GUÍA FRONTEND: MFA OBLIGATORIO PARA ADMIN Y EXPERTOS

**Framework:** React + TypeScript  
**Fecha:** Noviembre 2025  
**Nivel:** Implementación completa según Best Practices 2025  

---

## 📋 ÍNDICE

1. [Resumen ejecutivo](#1-resumen-ejecutivo)
2. [Arquitectura del sistema](#2-arquitectura-del-sistema)
3. [Implementación paso a paso](#3-implementación-paso-a-paso)
4. [Componentes principales](#4-componentes-principales)
5. [Flujos completos](#5-flujos-completos)
6. [Best practices 2025](#6-best-practices-2025)
7. [Testing](#7-testing)

---

## 1. RESUMEN EJECUTIVO

### 🎯 Objetivo

Hacer que MFA sea **OBLIGATORIO** para:
- ✅ **Administradores** (Admin role = 2)
- ✅ **Expertos** (Expert role = 1)
- ⚠️ **Clientes** (Client role = 0) - Opcional

### 🔄 Flujo de autenticación

```
┌─────────────────────────────────────────────────────────┐
│                     FLUJO COMPLETO                      │
└─────────────────────────────────────────────────────────┘

1. Usuario → Google Login
2. Backend → Verifica y devuelve tokens + rol
3. Frontend → Detecta si es Admin/Expert
4. Frontend → Verifica si tiene MFA habilitado
   
   ┌─────────────────────────┐
   │ ¿Tiene MFA habilitado?  │
   └─────────────────────────┘
            │
    ┌───────┴───────┐
    │               │
   NO              SÍ
    │               │
    ▼               ▼
┌────────┐    ┌──────────┐
│ FORZAR │    │ VERIFICAR│
│ SETUP  │    │  CÓDIGO  │
└────────┘    └──────────┘
    │               │
    │               │
    └───────┬───────┘
            ▼
    ┌──────────────┐
    │   DASHBOARD  │
    └──────────────┘
```

---

## 2. ARQUITECTURA DEL SISTEMA

### 📂 Estructura de archivos

```
src/
├── services/
│   ├── authService.ts          # ✅ Ya implementado
│   ├── mfaService.ts           # ✅ Ya implementado
│   └── rateLimitHandler.ts    # ✅ Ya implementado
├── hooks/
│   ├── useAuth.tsx             # ⚠️ Modificar
│   ├── useMfaEnforcement.tsx  # 🆕 Crear
│   └── useProtectedRoute.tsx  # 🆕 Crear
├── components/
│   ├── auth/
│   │   ├── GoogleAuth.tsx      # ⚠️ Modificar
│   │   ├── MFASetup.tsx        # ✅ Ya implementado
│   │   ├── MFAVerify.tsx       # ✅ Ya implementado
│   │   └── MFAGuard.tsx        # 🆕 Crear
│   └── layout/
│       ├── ProtectedRoute.tsx  # 🆕 Crear
│       └── MFABanner.tsx       # 🆕 Crear
├── pages/
│   ├── LoginPage.tsx           # ⚠️ Modificar
│   ├── MFASetupPage.tsx        # 🆕 Crear (obligatorio)
│   └── Dashboard.tsx           # ⚠️ Agregar banner
└── utils/
    ├── roleChecker.ts          # 🆕 Crear
    └── mfaEnforcement.ts       # 🆕 Crear
```

---

## 3. IMPLEMENTACIÓN PASO A PASO

### PASO 1: Crear utilidad de verificación de roles

**Archivo:** `src/utils/roleChecker.ts`

```typescript
import jwtDecode from 'jwt-decode';

export enum UserRole {
  Client = 0,
  Expert = 1,
  Admin = 2
}

export interface DecodedToken {
  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': string;
  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress': string;
  'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name': string;
  'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': string;
  exp: number;
  iat: number;
  jti: string;
}

/**
 * ✅ BEST PRACTICE 2025: Type-safe role checking
 */
export class RoleChecker {
  private static readonly ROLE_CLAIM = 
    'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

  /**
   * Obtiene el rol del usuario desde el token
   */
  static getUserRole(token: string): UserRole | null {
    try {
      const decoded = jwtDecode<DecodedToken>(token);
      const roleString = decoded[this.ROLE_CLAIM];
      
      // Convertir string a enum
      switch (roleString) {
        case 'Client': return UserRole.Client;
        case 'Expert': return UserRole.Expert;
        case 'Admin': return UserRole.Admin;
        default: return null;
      }
    } catch (error) {
      console.error('Error decoding token:', error);
      return null;
    }
  }

  /**
   * Verifica si el rol requiere MFA obligatorio
   */
  static requiresMfa(role: UserRole | null): boolean {
    if (role === null) return false;
    return role === UserRole.Admin || role === UserRole.Expert;
  }

  /**
   * Obtiene el nombre del rol en español
   */
  static getRoleName(role: UserRole): string {
    switch (role) {
      case UserRole.Client: return 'Cliente';
      case UserRole.Expert: return 'Experto';
      case UserRole.Admin: return 'Administrador';
      default: return 'Desconocido';
    }
  }

  /**
   * Verifica si el usuario es Admin
   */
  static isAdmin(token: string): boolean {
    return this.getUserRole(token) === UserRole.Admin;
  }

  /**
   * Verifica si el usuario es Expert
   */
  static isExpert(token: string): boolean {
    return this.getUserRole(token) === UserRole.Expert;
  }

  /**
   * Verifica si el usuario es Client
   */
  static isClient(token: string): boolean {
    return this.getUserRole(token) === UserRole.Client;
  }
}
```

---

### PASO 2: Crear hook de enforcement de MFA

**Archivo:** `src/hooks/useMfaEnforcement.tsx`

```typescript
import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { authService } from '../services/authService';
import { mfaService } from '../services/mfaService';
import { RoleChecker, UserRole } from '../utils/roleChecker';

export interface MfaEnforcementState {
  isLoading: boolean;
  requiresSetup: boolean;
  requiresVerification: boolean;
  userRole: UserRole | null;
  gracePeriodDays: number | null;
  isEnforced: boolean;
}

/**
 * ✅ BEST PRACTICE 2025: Custom hook para gestión de MFA obligatorio
 * 
 * Este hook maneja la lógica de enforcement de MFA:
 * - Detecta si el usuario requiere MFA
 * - Verifica si ya lo tiene configurado
 * - Calcula período de gracia (3 días)
 * - Redirige a setup si es necesario
 */
export function useMfaEnforcement() {
  const navigate = useNavigate();
  const [state, setState] = useState<MfaEnforcementState>({
    isLoading: true,
    requiresSetup: false,
    requiresVerification: false,
    userRole: null,
    gracePeriodDays: null,
    isEnforced: false
  });

  useEffect(() => {
    checkMfaEnforcement();
  }, []);

  const checkMfaEnforcement = async () => {
    try {
      // 1. Obtener token actual
      const token = authService.getAccessToken();
      if (!token) {
        setState(prev => ({ ...prev, isLoading: false }));
        return;
      }

      // 2. Verificar rol del usuario
      const userRole = RoleChecker.getUserRole(token);
      
      // 3. Verificar si este rol requiere MFA
      const requiresMfa = RoleChecker.requiresMfa(userRole);
      
      if (!requiresMfa) {
        // Cliente → No requiere MFA
        setState({
          isLoading: false,
          requiresSetup: false,
          requiresVerification: false,
          userRole,
          gracePeriodDays: null,
          isEnforced: false
        });
        return;
      }

      // 4. Verificar estado de MFA en el servidor
      const mfaStatus = await mfaService.getMFAStatus();

      if (!mfaStatus.isEnabled) {
        // MFA NO habilitado → Verificar período de gracia
        const accountCreatedAt = await getAccountCreationDate();
        const daysSinceCreation = calculateDaysSince(accountCreatedAt);
        const gracePeriod = 3; // 3 días de gracia
        const remainingDays = Math.max(0, gracePeriod - daysSinceCreation);

        setState({
          isLoading: false,
          requiresSetup: true,
          requiresVerification: false,
          userRole,
          gracePeriodDays: remainingDays,
          isEnforced: remainingDays === 0
        });

        // Si el período de gracia expiró → Forzar setup
        if (remainingDays === 0) {
          navigate('/mfa/setup-required', { 
            state: { reason: 'grace_period_expired' } 
          });
        }
      } else {
        // MFA habilitado → Todo OK
        setState({
          isLoading: false,
          requiresSetup: false,
          requiresVerification: false,
          userRole,
          gracePeriodDays: null,
          isEnforced: false
        });
      }
    } catch (error) {
      console.error('Error checking MFA enforcement:', error);
      setState(prev => ({ ...prev, isLoading: false }));
    }
  };

  /**
   * Obtiene la fecha de creación de la cuenta
   */
  const getAccountCreationDate = async (): Promise<Date> => {
    // Implementar según tu API
    // Por ahora, usamos una fecha mock
    try {
      const response = await fetch('/api/user/profile', {
        headers: {
          'Authorization': `Bearer ${authService.getAccessToken()}`
        }
      });
      const data = await response.json();
      return new Date(data.createdAt);
    } catch (error) {
      // Fallback: asumir cuenta nueva (forzar MFA inmediatamente)
      return new Date();
    }
  };

  /**
   * Calcula días transcurridos desde una fecha
   */
  const calculateDaysSince = (date: Date): number => {
    const now = new Date();
    const diffTime = Math.abs(now.getTime() - date.getTime());
    const diffDays = Math.ceil(diffTime / (1000 * 60 * 60 * 24));
    return diffDays;
  };

  /**
   * Fuerza el setup de MFA (admin puede llamar esto)
   */
  const forceSetup = () => {
    navigate('/mfa/setup-required', { 
      state: { reason: 'admin_enforced' } 
    });
  };

  return {
    ...state,
    checkMfaEnforcement,
    forceSetup
  };
}
```

---

### PASO 3: Crear componente de ruta protegida

**Archivo:** `src/components/layout/ProtectedRoute.tsx`

```typescript
import React from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { authService } from '../../services/authService';
import { RoleChecker, UserRole } from '../../utils/roleChecker';
import { useMfaEnforcement } from '../../hooks/useMfaEnforcement';

interface ProtectedRouteProps {
  children: React.ReactNode;
  requireMfa?: boolean;
  allowedRoles?: UserRole[];
}

/**
 * ✅ BEST PRACTICE 2025: Route protection con MFA enforcement
 * 
 * Este componente protege rutas y verifica:
 * 1. Autenticación (tiene token válido)
 * 2. Autorización (rol permitido)
 * 3. MFA (si es requerido)
 */
export const ProtectedRoute: React.FC<ProtectedRouteProps> = ({
  children,
  requireMfa = false,
  allowedRoles
}) => {
  const location = useLocation();
  const { isLoading, requiresSetup, isEnforced, userRole } = useMfaEnforcement();

  // 1. Verificar autenticación
  const token = authService.getAccessToken();
  if (!token) {
    return <Navigate to="/login" state={{ from: location }} replace />;
  }

  // 2. Verificar autorización (rol)
  if (allowedRoles && userRole) {
    if (!allowedRoles.includes(userRole)) {
      return <Navigate to="/unauthorized" replace />;
    }
  }

  // 3. Verificar MFA (si es requerido)
  if (requireMfa || RoleChecker.requiresMfa(userRole)) {
    if (isLoading) {
      return (
        <div className="flex items-center justify-center min-h-screen">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-primary"></div>
        </div>
      );
    }

    if (requiresSetup && isEnforced) {
      // MFA obligatorio y no configurado → Redirigir a setup
      return <Navigate to="/mfa/setup-required" replace />;
    }
  }

  // ✅ Todo OK → Renderizar contenido
  return <>{children}</>;
};
```

---

### PASO 4: Crear banner de advertencia MFA

**Archivo:** `src/components/layout/MFABanner.tsx`

```typescript
import React, { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { useMfaEnforcement } from '../../hooks/useMfaEnforcement';
import { RoleChecker } from '../../utils/roleChecker';

/**
 * ✅ BEST PRACTICE 2025: Progressive disclosure
 * 
 * Banner que muestra advertencias graduales sobre MFA:
 * - Día 1-2: Advertencia informativa
 * - Día 3: Advertencia urgente
 * - Día 4+: Bloqueado (redirigido por ProtectedRoute)
 */
export const MFABanner: React.FC = () => {
  const { requiresSetup, gracePeriodDays, userRole } = useMfaEnforcement();
  const [isDismissed, setIsDismissed] = useState(false);

  useEffect(() => {
    // Recuperar estado de dismissal desde localStorage
    const dismissed = localStorage.getItem('mfa-banner-dismissed');
    if (dismissed === 'true') {
      setIsDismissed(true);
    }
  }, []);

  // No mostrar si:
  // 1. No requiere setup
  // 2. Usuario lo dismissió
  // 3. No hay período de gracia (ya configuró MFA)
  if (!requiresSetup || isDismissed || gracePeriodDays === null) {
    return null;
  }

  const handleDismiss = () => {
    setIsDismissed(true);
    localStorage.setItem('mfa-banner-dismissed', 'true');
  };

  // Determinar severidad según días restantes
  const getSeverityLevel = (): 'info' | 'warning' | 'critical' => {
    if (gracePeriodDays >= 2) return 'info';
    if (gracePeriodDays === 1) return 'warning';
    return 'critical';
  };

  const severity = getSeverityLevel();
  const roleName = userRole ? RoleChecker.getRoleName(userRole) : '';

  // Estilos según severidad
  const bannerStyles = {
    info: 'bg-blue-50 border-blue-500 text-blue-900',
    warning: 'bg-yellow-50 border-yellow-500 text-yellow-900',
    critical: 'bg-red-50 border-red-500 text-red-900 animate-pulse'
  };

  const iconStyles = {
    info: '💡',
    warning: '⚠️',
    critical: '🚨'
  };

  return (
    <div 
      className={`
        border-l-4 p-4 mb-4 
        ${bannerStyles[severity]}
        relative
      `}
      role="alert"
    >
      <div className="flex items-start">
        <div className="flex-shrink-0 text-2xl mr-3">
          {iconStyles[severity]}
        </div>
        
        <div className="flex-1">
          <h3 className="font-bold text-lg mb-1">
            {severity === 'critical' 
              ? '🚨 ACCIÓN REQUERIDA: Configura MFA HOY' 
              : `MFA Requerido para ${roleName}s`
            }
          </h3>
          
          <p className="mb-2">
            Como {roleName}, debes habilitar la Autenticación de Dos Factores (MFA) 
            para proteger tu cuenta y los datos que manejas.
          </p>

          {gracePeriodDays > 0 ? (
            <p className="mb-3">
              <strong>
                Tienes {gracePeriodDays} día{gracePeriodDays !== 1 ? 's' : ''} restante{gracePeriodDays !== 1 ? 's' : ''}
              </strong> para configurarlo.
              {gracePeriodDays === 1 && ' Después de esto, no podrás acceder a la plataforma sin MFA.'}
            </p>
          ) : (
            <p className="mb-3 font-bold">
              El período de gracia ha expirado. Debes configurar MFA ahora mismo.
            </p>
          )}

          <div className="flex gap-3">
            <Link
              to="/mfa/setup"
              className="inline-flex items-center px-4 py-2 bg-primary text-white rounded-md hover:bg-primary-dark transition-colors"
            >
              Configurar MFA ahora →
            </Link>
            
            {severity !== 'critical' && (
              <button
                onClick={handleDismiss}
                className="inline-flex items-center px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
              >
                Recordar más tarde
              </button>
            )}
          </div>
        </div>

        {/* Botón de cerrar (solo para info y warning) */}
        {severity !== 'critical' && (
          <button
            onClick={handleDismiss}
            className="flex-shrink-0 ml-4 text-gray-400 hover:text-gray-600"
            aria-label="Cerrar"
          >
            ✕
          </button>
        )}
      </div>
    </div>
  );
};
```

---

### PASO 5: Crear página de setup obligatorio

**Archivo:** `src/pages/MFASetupPage.tsx`

```typescript
import React, { useState } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { MFASetup } from '../components/auth/MFASetup';
import { RoleChecker } from '../utils/roleChecker';
import { authService } from '../services/authService';

/**
 * ✅ BEST PRACTICE 2025: Dedicated onboarding page
 * 
 * Página de setup obligatorio de MFA con:
 * - Explicación clara del porqué
 * - Paso a paso guiado
 * - No permite saltar (si es obligatorio)
 */
export const MFASetupPage: React.FC = () => {
  const navigate = useNavigate();
  const location = useLocation();
  const [isCompleted, setIsCompleted] = useState(false);

  // Obtener razón de la redirección
  const reason = location.state?.reason;
  const isRequired = reason === 'grace_period_expired' || reason === 'admin_enforced';

  // Obtener rol del usuario
  const token = authService.getAccessToken();
  const userRole = token ? RoleChecker.getUserRole(token) : null;
  const roleName = userRole ? RoleChecker.getRoleName(userRole) : 'Usuario';

  const handleSetupComplete = () => {
    setIsCompleted(true);
    
    // Mostrar mensaje de éxito
    setTimeout(() => {
      navigate('/dashboard', { 
        state: { message: '✅ MFA configurado exitosamente' } 
      });
    }, 2000);
  };

  if (isCompleted) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8 text-center">
          <div className="text-6xl mb-4">✅</div>
          <h2 className="text-2xl font-bold text-gray-900 mb-2">
            ¡MFA Configurado!
          </h2>
          <p className="text-gray-600 mb-4">
            Tu cuenta ahora está protegida con autenticación de dos factores.
          </p>
          <p className="text-sm text-gray-500">
            Redirigiendo al dashboard...
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50 py-8">
      <div className="max-w-4xl mx-auto px-4">
        {/* Header con contexto */}
        <div className={`
          rounded-lg p-6 mb-6
          ${isRequired 
            ? 'bg-red-50 border-2 border-red-500' 
            : 'bg-blue-50 border-2 border-blue-500'
          }
        `}>
          <div className="flex items-start">
            <div className="flex-shrink-0 text-4xl mr-4">
              {isRequired ? '🔒' : '🔐'}
            </div>
            <div className="flex-1">
              <h1 className="text-2xl font-bold text-gray-900 mb-2">
                {isRequired 
                  ? 'Configuración Obligatoria de MFA' 
                  : 'Configura la Autenticación de Dos Factores'
                }
              </h1>
              
              <p className="text-gray-700 mb-3">
                Como <strong>{roleName}</strong>, necesitas habilitar MFA para:
              </p>

              <ul className="list-disc list-inside space-y-1 text-gray-700 mb-4">
                <li>Proteger tu cuenta contra accesos no autorizados</li>
                <li>Cumplir con las normativas de seguridad (GDPR, PCI DSS)</li>
                <li>Proteger los datos que manejas</li>
                {userRole === 1 && <li>Proteger tus pagos y cuenta de Stripe</li>}
                {userRole === 2 && <li>Proteger el acceso administrativo al sistema</li>}
              </ul>

              {isRequired && (
                <div className="bg-white rounded-md p-3 border border-red-300">
                  <p className="text-sm text-red-800 font-semibold">
                    ⚠️ No podrás acceder a la plataforma sin completar este paso.
                  </p>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Información adicional */}
        <div className="bg-white rounded-lg shadow-sm p-6 mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">
            📱 ¿Qué necesitas?
          </h2>
          
          <div className="grid md:grid-cols-2 gap-4">
            <div className="flex items-start">
              <div className="flex-shrink-0 text-2xl mr-3">1️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">Una app de autenticación</h3>
                <p className="text-sm text-gray-600">
                  Google Authenticator, Microsoft Authenticator, Authy, o similar
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="flex-shrink-0 text-2xl mr-3">2️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">Tu teléfono</h3>
                <p className="text-sm text-gray-600">
                  Para escanear el código QR o ingresar la clave manualmente
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="flex-shrink-0 text-2xl mr-3">3️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">3 minutos</h3>
                <p className="text-sm text-gray-600">
                  El proceso es rápido y sencillo
                </p>
              </div>
            </div>
            
            <div className="flex items-start">
              <div className="flex-shrink-0 text-2xl mr-3">4️⃣</div>
              <div>
                <h3 className="font-semibold mb-1">Un lugar seguro</h3>
                <p className="text-sm text-gray-600">
                  Para guardar tus códigos de recuperación
                </p>
              </div>
            </div>
          </div>
        </div>

        {/* Componente de setup */}
        <div className="bg-white rounded-lg shadow-lg p-6">
          <MFASetup onComplete={handleSetupComplete} />
        </div>

        {/* Soporte */}
        <div className="mt-6 text-center text-sm text-gray-500">
          <p>
            ¿Necesitas ayuda? {' '}
            <a href="/help/mfa" className="text-primary hover:underline">
              Ver guía completa
            </a>
            {' '} o {' '}
            <a href="/support" className="text-primary hover:underline">
              contacta a soporte
            </a>
          </p>
        </div>
      </div>
    </div>
  );
};
```

---

### PASO 6: Modificar GoogleAuth para detectar MFA obligatorio

**Archivo:** `src/components/auth/GoogleAuth.tsx` (modificar)

```typescript
import React, { useState } from 'react';
import { GoogleLogin } from '@react-oauth/google';
import { useNavigate } from 'react-router-dom';
import { authService } from '../../services/authService';
import { mfaService } from '../../services/mfaService';
import { RoleChecker } from '../../utils/roleChecker';
import { MFAVerify } from './MFAVerify';

export const GoogleAuth: React.FC = () => {
  const navigate = useNavigate();
  const [showMfaVerify, setShowMfaVerify] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleGoogleSuccess = async (credentialResponse: any) => {
    setLoading(true);
    setError(null);

    try {
      // 1. Autenticar con Google
      const result = await authService.googleAuth(credentialResponse.credential);

      if (!result.success) {
        throw new Error('Authentication failed');
      }

      // 2. Verificar rol del usuario
      const token = authService.getAccessToken();
      if (!token) {
        throw new Error('No token received');
      }

      const userRole = RoleChecker.getUserRole(token);
      const requiresMfa = RoleChecker.requiresMfa(userRole);

      if (requiresMfa) {
        // 3. Verificar si tiene MFA habilitado
        const mfaStatus = await mfaService.getMFAStatus();

        if (!mfaStatus.isEnabled) {
          // ⚠️ MFA requerido pero NO configurado
          navigate('/mfa/setup-required', {
            state: { 
              reason: 'required_for_role',
              firstLogin: true 
            }
          });
          return;
        }

        // ✅ MFA configurado → Solicitar verificación
        setShowMfaVerify(true);
        return;
      }

      // Cliente → Login directo
      navigate('/dashboard');

    } catch (err: any) {
      console.error('Google Auth error:', err);
      setError(err.response?.data?.message || err.message || 'Error al iniciar sesión');
    } finally {
      setLoading(false);
    }
  };

  const handleMfaSuccess = () => {
    navigate('/dashboard');
  };

  const handleMfaCancel = () => {
    authService.logout();
    setShowMfaVerify(false);
  };

  // Mostrar verificación MFA
  if (showMfaVerify) {
    return <MFAVerify onSuccess={handleMfaSuccess} onCancel={handleMfaCancel} />;
  }

  // Login normal
  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8">
        <h1 className="text-3xl font-bold text-center mb-6">Iniciar Sesión</h1>
        
        {error && (
          <div className="mb-4 p-3 bg-red-50 border border-red-300 rounded-md">
            <p className="text-sm text-red-800">{error}</p>
          </div>
        )}

        <div className="flex justify-center">
          <GoogleLogin
            onSuccess={handleGoogleSuccess}
            onError={() => setError('Error al iniciar sesión con Google')}
            useOneTap
          />
        </div>

        {loading && (
          <div className="mt-4 text-center">
            <div className="inline-block animate-spin rounded-full h-8 w-8 border-b-2 border-primary"></div>
            <p className="mt-2 text-sm text-gray-600">Iniciando sesión...</p>
          </div>
        )}
      </div>
    </div>
  );
};
```

---

### PASO 7: Agregar banner al Dashboard

**Archivo:** `src/pages/Dashboard.tsx` (modificar)

```typescript
import React from 'react';
import { MFABanner } from '../components/layout/MFABanner';

export const Dashboard: React.FC = () => {
  return (
    <div className="min-h-screen bg-gray-50">
      {/* ⚠️ Banner de advertencia MFA */}
      <MFABanner />

      {/* Resto del dashboard */}
      <div className="max-w-7xl mx-auto py-6 px-4">
        <h1 className="text-3xl font-bold text-gray-900 mb-6">Dashboard</h1>
        
        {/* Contenido del dashboard */}
      </div>
    </div>
  );
};
```

---

### PASO 8: Configurar rutas protegidas

**Archivo:** `src/App.tsx` (modificar)

```typescript
import React, { useEffect } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { authService } from './services/authService';
import { setupRateLimitHandler } from './services/rateLimitHandler';
import { ProtectedRoute } from './components/layout/ProtectedRoute';
import { UserRole } from './utils/roleChecker';

// Pages
import { LoginPage } from './pages/LoginPage';
import { Dashboard } from './pages/Dashboard';
import { MFASetupPage } from './pages/MFASetupPage';
import { AdminPanel } from './pages/AdminPanel';
import { ExpertDashboard } from './pages/ExpertDashboard';

function App() {
  useEffect(() => {
    // Inicializar servicios de seguridad
    authService.initFromStorage();
    authService.setupAxiosInterceptor();
    setupRateLimitHandler();
  }, []);

  return (
    <BrowserRouter>
      <Routes>
        {/* Rutas públicas */}
        <Route path="/login" element={<LoginPage />} />
        <Route path="/unauthorized" element={<UnauthorizedPage />} />

        {/* Rutas de MFA */}
        <Route 
          path="/mfa/setup" 
          element={
            <ProtectedRoute>
              <MFASetupPage />
            </ProtectedRoute>
          } 
        />
        <Route 
          path="/mfa/setup-required" 
          element={
            <ProtectedRoute>
              <MFASetupPage />
            </ProtectedRoute>
          } 
        />

        {/* Dashboard general (todos los roles) */}
        <Route 
          path="/dashboard" 
          element={
            <ProtectedRoute requireMfa>
              <Dashboard />
            </ProtectedRoute>
          } 
        />

        {/* Panel de administración (solo Admin) */}
        <Route 
          path="/admin/*" 
          element={
            <ProtectedRoute 
              requireMfa 
              allowedRoles={[UserRole.Admin]}
            >
              <AdminPanel />
            </ProtectedRoute>
          } 
        />

        {/* Dashboard de expertos (solo Expert) */}
        <Route 
          path="/expert/*" 
          element={
            <ProtectedRoute 
              requireMfa 
              allowedRoles={[UserRole.Expert]}
            >
              <ExpertDashboard />
            </ProtectedRoute>
          } 
        />

        {/* Redireccionamiento por defecto */}
        <Route path="/" element={<Navigate to="/dashboard" replace />} />
        <Route path="*" element={<NotFoundPage />} />
      </Routes>
    </BrowserRouter>
  );
}

export default App;
```

---

## 4. COMPONENTES PRINCIPALES

### Resumen de componentes creados/modificados:

| Componente | Tipo | Estado | Propósito |
|------------|------|--------|-----------|
| `RoleChecker` | Utility | 🆕 | Verificación type-safe de roles |
| `useMfaEnforcement` | Hook | 🆕 | Lógica de enforcement MFA |
| `ProtectedRoute` | Component | 🆕 | Protección de rutas |
| `MFABanner` | Component | 🆕 | Advertencias graduales |
| `MFASetupPage` | Page | 🆕 | Setup obligatorio |
| `GoogleAuth` | Component | ⚠️ | Detección de MFA requerido |
| `Dashboard` | Page | ⚠️ | Agregar banner |
| `App.tsx` | Root | ⚠️ | Configurar rutas |

---

## 5. FLUJOS COMPLETOS

### FLUJO 1: Admin sin MFA (Primera vez)

```
1. Admin → Login con Google
2. authService.googleAuth() → Éxito
3. RoleChecker.getUserRole() → Admin (2)
4. RoleChecker.requiresMfa() → true
5. mfaService.getMFAStatus() → isEnabled: false
6. Navigate → /mfa/setup-required
7. MFASetupPage → Mostrar explicación + setup
8. Usuario configura MFA
9. Navigate → /dashboard ✅
```

### FLUJO 2: Expert con MFA (Login diario)

```
1. Expert → Login con Google
2. authService.googleAuth() → Éxito
3. RoleChecker.getUserRole() → Expert (1)
4. mfaService.getMFAStatus() → isEnabled: true
5. Mostrar MFAVerify component
6. Usuario ingresa código
7. mfaService.verifyMFA() → Éxito
8. Navigate → /dashboard ✅
```

### FLUJO 3: Cliente (Sin MFA)

```
1. Cliente → Login con Google
2. authService.googleAuth() → Éxito
3. RoleChecker.getUserRole() → Client (0)
4. RoleChecker.requiresMfa() → false
5. Navigate → /dashboard ✅ (sin MFA)
```

### FLUJO 4: Expert sin MFA (Período de gracia)

```
1. Expert → Login con Google (día 1-2)
2. Navigate → /dashboard
3. MFABanner → Muestra advertencia azul
4. "Tienes 2 días restantes para configurar MFA"
5. Usuario puede dismissar banner
6. Aplicación funciona normal ✅

---

7. Expert → Login (día 3)
8. MFABanner → Advertencia roja (crítica)
9. "Tienes 1 día restante"
10. No se puede dismissar
11. Aplicación funciona normal ⚠️

---

12. Expert → Login (día 4+)
13. ProtectedRoute → Detecta enforcement
14. Navigate → /mfa/setup-required 🔒
15. NO puede acceder sin MFA
```

---

## 6. BEST PRACTICES 2025

### ✅ Lo que ya tienes implementado:

1. **Type-safe role checking** con TypeScript
2. **Progressive disclosure** (advertencias graduales)
3. **Graceful degradation** (período de gracia)
4. **Clear error messages** (mensajes descriptivos)
5. **Non-blocking flows** (cliente sigue sin MFA)
6. **Security by default** (Admin/Expert protegidos)

### 🎯 Recomendaciones adicionales:

#### 1. **Analytics y Monitoring**

```typescript
// Trackear eventos de MFA
import { analytics } from './analytics';

// En useMfaEnforcement.tsx
const trackMfaEvent = (event: string, data?: any) => {
  analytics.track('mfa_event', {
    event,
    userRole,
    timestamp: new Date().toISOString(),
    ...data
  });
};

// Ejemplos:
trackMfaEvent('mfa_setup_started');
trackMfaEvent('mfa_setup_completed');
trackMfaEvent('mfa_verification_failed', { attemptNumber: 3 });
trackMfaEvent('mfa_grace_period_warning', { daysRemaining: 1 });
```

#### 2. **Error Boundaries**

```typescript
// components/ErrorBoundary.tsx
import React, { Component, ErrorInfo, ReactNode } from 'react';

interface Props {
  children: ReactNode;
  fallback?: ReactNode;
}

interface State {
  hasError: boolean;
  error?: Error;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('MFA Error:', error, errorInfo);
    
    // Reportar a servicio de monitoreo
    if (window.Sentry) {
      window.Sentry.captureException(error);
    }
  }

  render() {
    if (this.state.hasError) {
      return this.props.fallback || (
        <div className="min-h-screen flex items-center justify-center bg-gray-50">
          <div className="text-center">
            <h2 className="text-2xl font-bold mb-4">Algo salió mal</h2>
            <p className="text-gray-600 mb-4">
              Hubo un error al cargar la autenticación.
            </p>
            <button 
              onClick={() => window.location.reload()}
              className="px-4 py-2 bg-primary text-white rounded-md"
            >
              Recargar página
            </button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

// Uso en App.tsx
<ErrorBoundary>
  <ProtectedRoute>
    <Dashboard />
  </ProtectedRoute>
</ErrorBoundary>
```

#### 3. **Accessibility (A11y)**

```typescript
// Agregar ARIA labels y roles
<div 
  role="alert" 
  aria-live="polite"
  aria-atomic="true"
>
  <h3 id="mfa-banner-title">MFA Requerido</h3>
  <p id="mfa-banner-description">
    Como {roleName}, debes habilitar MFA...
  </p>
</div>

// Keyboard navigation
<button
  onClick={handleSetup}
  onKeyPress={(e) => e.key === 'Enter' && handleSetup()}
  aria-label="Configurar autenticación de dos factores"
>
  Configurar MFA
</button>
```

#### 4. **Loading States**

```typescript
// Skeleton screens durante carga
const LoadingSkeleton = () => (
  <div className="animate-pulse space-y-4">
    <div className="h-4 bg-gray-200 rounded w-3/4"></div>
    <div className="h-4 bg-gray-200 rounded w-1/2"></div>
    <div className="h-32 bg-gray-200 rounded"></div>
  </div>
);

// En ProtectedRoute
if (isLoading) {
  return <LoadingSkeleton />;
}
```

#### 5. **Offline Support**

```typescript
// Detectar estado offline
const useOnlineStatus = () => {
  const [isOnline, setIsOnline] = useState(navigator.onLine);

  useEffect(() => {
    const handleOnline = () => setIsOnline(true);
    const handleOffline = () => setIsOnline(false);

    window.addEventListener('online', handleOnline);
    window.addEventListener('offline', handleOffline);

    return () => {
      window.removeEventListener('online', handleOnline);
      window.removeEventListener('offline', handleOffline);
    };
  }, []);

  return isOnline;
};

// En componentes críticos
const { isOnline } = useOnlineStatus();

if (!isOnline) {
  return (
    <div className="bg-yellow-50 p-4">
      ⚠️ Sin conexión. Algunas funciones pueden no estar disponibles.
    </div>
  );
}
```

---

## 7. TESTING

### Unit Tests (Jest + React Testing Library)

```typescript
// __tests__/RoleChecker.test.ts
import { RoleChecker, UserRole } from '../utils/roleChecker';

describe('RoleChecker', () => {
  const mockToken = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...';

  it('should detect Admin role', () => {
    const role = RoleChecker.getUserRole(mockToken);
    expect(role).toBe(UserRole.Admin);
  });

  it('should require MFA for Admin', () => {
    expect(RoleChecker.requiresMfa(UserRole.Admin)).toBe(true);
  });

  it('should not require MFA for Client', () => {
    expect(RoleChecker.requiresMfa(UserRole.Client)).toBe(false);
  });
});
```

```typescript
// __tests__/useMfaEnforcement.test.tsx
import { renderHook, waitFor } from '@testing-library/react';
import { useMfaEnforcement } from '../hooks/useMfaEnforcement';

describe('useMfaEnforcement', () => {
  it('should detect when MFA is required', async () => {
    const { result } = renderHook(() => useMfaEnforcement());

    await waitFor(() => {
      expect(result.current.isLoading).toBe(false);
    });

    expect(result.current.requiresSetup).toBe(true);
  });
});
```

### Integration Tests (Cypress)

```typescript
// cypress/e2e/mfa-enforcement.cy.ts
describe('MFA Enforcement Flow', () => {
  it('should force Admin to setup MFA', () => {
    // 1. Login como Admin
    cy.loginAsAdmin();

    // 2. Verificar redirección a setup
    cy.url().should('include', '/mfa/setup-required');

    // 3. Verificar mensaje
    cy.contains('Configuración Obligatoria de MFA');

    // 4. Intentar navegar a dashboard → debe bloquear
    cy.visit('/dashboard');
    cy.url().should('include', '/mfa/setup-required');
  });

  it('should show grace period banner for Expert', () => {
    // 1. Login como Expert (nuevo)
    cy.loginAsExpert();

    // 2. Verificar banner
    cy.contains('Tienes 3 días restantes');

    // 3. Puede acceder al dashboard
    cy.url().should('include', '/dashboard');
  });
});
```

---

## 8. TROUBLESHOOTING

### Problema 1: "Loop infinito entre login y MFA setup"

**Causa:** Token no se guarda correctamente después de Google Auth.

**Solución:**
```typescript
// Verificar en authService.googleAuth()
const [accessToken, refreshToken] = response.data.token.split('|');
this.setTokens(accessToken, refreshToken); // ← Asegurar que esto se ejecute
```

### Problema 2: "Banner aparece aunque ya configuré MFA"

**Causa:** localStorage guarda estado de dismissal antiguo.

**Solución:**
```typescript
// Limpiar localStorage después de configurar MFA
localStorage.removeItem('mfa-banner-dismissed');
```

### Problema 3: "No detecta el rol correctamente"

**Causa:** Claim del rol usa namespace largo de Microsoft.

**Solución:**
```typescript
const ROLE_CLAIM = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const roleString = decoded[ROLE_CLAIM]; // ← Usar claim completo
```

---

## 9. RESUMEN Y CHECKLIST

### ✅ Checklist de implementación:

- [ ] Crear `RoleChecker.ts`
- [ ] Crear `useMfaEnforcement.tsx`
- [ ] Crear `ProtectedRoute.tsx`
- [ ] Crear `MFABanner.tsx`
- [ ] Crear `MFASetupPage.tsx`
- [ ] Modificar `GoogleAuth.tsx`
- [ ] Modificar `Dashboard.tsx`
- [ ] Configurar rutas en `App.tsx`
- [ ] Agregar tests
- [ ] Probar flujos completos

### 📊 Tiempo estimado:

- Setup inicial: 2-3 horas
- Testing: 1-2 horas
- Refinamiento: 1 hora
- **Total: 4-6 horas**

### 🎯 Resultado final:

```
✅ Admin → MFA obligatorio (inmediato)
✅ Expert → MFA obligatorio (3 días de gracia)
✅ Client → MFA opcional
✅ Advertencias graduales
✅ Rutas protegidas
✅ Type-safe con TypeScript
✅ Accesible (A11y)
✅ Testeable
✅ Best practices 2025
```

---

## 📞 SOPORTE

**¿Dudas durante la implementación?**

1. Revisa `FRONTEND_COMPLETE_GUIDE.md` para más detalles
2. Consulta `SECURITY_AUDIT_2025.md` para contexto
3. Ver ejemplos en `MFA_COMPLETE_IMPLEMENTATION.md`

**¡Buena suerte con la implementación!** 🚀


