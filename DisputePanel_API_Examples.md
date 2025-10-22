# 🚨 Panel de Administración de Disputas - API

## 📋 **Endpoints Disponibles**

### 1️⃣ **Obtener Todas las Disputas (Admin)**
```
GET /api/dispute/all
```

**Parámetros de consulta:**
```javascript
{
  page: 1,                    // Página actual
  pageSize: 20,              // Resultados por página (máx 50)
  searchTerm: "problema",    // Buscar en razón y comentarios
  status: "Pending",         // Filtrar por estado
  reporterId: 123,           // Filtrar por usuario que reportó
  clientId: 456,             // Filtrar por cliente
  expertId: 789,             // Filtrar por experto
  startDate: "2024-01-01",   // Fecha de inicio
  endDate: "2024-12-31",     // Fecha de fin
  sortBy: "createdAt",       // Campo de ordenamiento
  sortDirection: "desc"      // Dirección (asc/desc)
}
```

**Respuesta:**
```json
{
  "disputes": [
    {
      "id": 1,
      "searchHireId": 15,
      "reporterId": 123,
      "reason": "El experto no cumplió con lo acordado",
      "status": "Pending",
      "statusTranslated": "Pendiente",
      "resolutionComments": null,
      "createdAt": "2024-01-15T10:30:00Z",
      "searchHire": {
        "id": 15,
        "status": "disputed",
        "statusTranslated": "En disputa",
        "amount": 150.00,
        "createdAt": "2024-01-10T09:00:00Z"
      },
      "reporter": {
        "id": 123,
        "name": "Juan Pérez",
        "email": "juan@email.com"
      },
      "client": {
        "id": 123,
        "name": "Juan Pérez",
        "email": "juan@email.com"
      },
      "expert": {
        "id": 456,
        "name": "María García",
        "email": "maria@email.com"
      },
      "search": {
        "id": 25,
        "title": "Búsqueda de coche usado",
        "description": "Necesito un coche económico",
        "createdAt": "2024-01-05T08:00:00Z"
      }
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 45,
    "totalPages": 3,
    "hasPrevious": false,
    "hasNext": true
  },
  "stats": {
    "pendingDisputes": 12,
    "resolvedDisputes": 33,
    "clientDisputes": 28,
    "expertDisputes": 17,
    "thisWeekDisputes": 5,
    "thisMonthDisputes": 18
  }
}
```

---

### 2️⃣ **Obtener Detalles de una Disputa**
```
GET /api/dispute/{disputeId}
```

**Respuesta:**
```json
{
  "id": 1,
  "searchHireId": 15,
  "reporterId": 123,
  "reason": "El experto no cumplió con lo acordado",
  "status": "Pending",
  "statusTranslated": "Pendiente",
  "resolutionComments": null,
  "createdAt": "2024-01-15T10:30:00Z",
  "searchHire": {
    "id": 15,
    "status": "disputed",
    "statusTranslated": "En disputa",
    "amount": 150.00,
    "createdAt": "2024-01-10T09:00:00Z"
  },
  "reporter": {
    "id": 123,
    "name": "Juan Pérez",
    "email": "juan@email.com"
  },
  "client": {
    "id": 123,
    "name": "Juan Pérez",
    "email": "juan@email.com"
  },
  "expert": {
    "id": 456,
    "name": "María García",
    "email": "maria@email.com"
  },
  "search": {
    "id": 25,
    "title": "Búsqueda de coche usado",
    "description": "Necesito un coche económico",
    "createdAt": "2024-01-05T08:00:00Z"
  }
}
```

---

### 3️⃣ **Obtener Búsqueda Completa desde Disputa**
```
GET /api/dispute/{disputeId}/search
```

**Respuesta:**
```json
{
  "id": 25,
  "userId": 123,
  "frequency": 7,
  "title": "Búsqueda de coche usado",
  "description": "Necesito un coche económico",
  "isActive": true,
  "lastExecution": "2024-01-15T08:00:00Z",
  "nextExecution": "2024-01-22T08:00:00Z",
  "isRevised": false,
  "createdAt": "2024-01-05T08:00:00Z",
  "startDate": "2024-01-05T08:00:00Z",
  "user": {
    "id": 123,
    "name": "Juan Pérez",
    "email": "juan@email.com"
  },
  "searchParameters": [
    {
      "id": 1,
      "searchId": 25,
      "category": 1,
      "locationName": "Madrid",
      "minPrice": 5000,
      "maxPrice": 15000,
      "createdAt": "2024-01-05T08:00:00Z"
    }
  ],
  "searchHire": {
    "id": 15,
    "expertId": 456,
    "status": "disputed",
    "statusTranslated": "En disputa",
    "createdAt": "2024-01-10T09:00:00Z",
    "expert": {
      "id": 456,
      "name": "María García",
      "email": "maria@email.com"
    },
    "service": {
      "id": 8,
      "serviceTypeName": "Búsqueda de Vehículos",
      "price": 150.00,
      "conditions": "Búsqueda en 3 plataformas",
      "durationInHours": 24,
      "imageUrls": ["https://example.com/image1.jpg"]
    }
  }
}
```

---

### 4️⃣ **Resolver una Disputa**
```
PUT /api/dispute/{disputeId}/resolve
```

**Body:**
```json
{
  "resolutionComments": "El experto no cumplió con las condiciones acordadas. Se procede al reembolso completo al cliente.",
  "action": "refund_client"
}
```

**Acciones disponibles:**
- `"refund_client"` - Reembolsar al cliente
- `"pay_expert"` - Pagar al experto
- `"no_action"` - No hacer nada financiero

**Respuesta:**
```json
{
  "message": "Dispute resolved successfully"
}
```

---

## 🎨 **Implementación en Frontend**

### **1. Componente Principal del Panel de Disputas**

```jsx
import React, { useState, useEffect } from 'react';

function AdminDisputePanel() {
  const [disputes, setDisputes] = useState([]);
  const [pagination, setPagination] = useState({});
  const [stats, setStats] = useState({});
  const [loading, setLoading] = useState(false);
  const [filters, setFilters] = useState({
    page: 1,
    pageSize: 20,
    searchTerm: '',
    status: '',
    reporterId: null,
    clientId: null,
    expertId: null,
    startDate: '',
    endDate: '',
    sortBy: 'createdAt',
    sortDirection: 'desc'
  });

  const loadDisputes = async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      Object.entries(filters).forEach(([key, value]) => {
        if (value !== null && value !== '') {
          params.append(key, value);
        }
      });

      const response = await fetch(`/api/dispute/all?${params}`, {
        headers: {
          'Authorization': `Bearer ${localStorage.getItem('token')}`
        }
      });

      if (response.ok) {
        const data = await response.json();
        setDisputes(data.disputes);
        setPagination(data.pagination);
        setStats(data.stats);
      }
    } catch (error) {
      console.error('Error loading disputes:', error);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadDisputes();
  }, [filters]);

  return (
    <div className="admin-dispute-panel">
      <h1>🚨 Panel de Disputas</h1>
      
      {/* Estadísticas */}
      <div className="stats-grid">
        <div className="stat-card pending">
          <h3>Pendientes</h3>
          <span className="stat-number">{stats.pendingDisputes}</span>
        </div>
        <div className="stat-card resolved">
          <h3>Resueltas</h3>
          <span className="stat-number">{stats.resolvedDisputes}</span>
        </div>
        <div className="stat-card this-week">
          <h3>Esta Semana</h3>
          <span className="stat-number">{stats.thisWeekDisputes}</span>
        </div>
        <div className="stat-card this-month">
          <h3>Este Mes</h3>
          <span className="stat-number">{stats.thisMonthDisputes}</span>
        </div>
      </div>

      {/* Filtros */}
      <div className="filters-section">
        <div className="filter-row">
          <input
            type="text"
            placeholder="Buscar en razón y comentarios..."
            value={filters.searchTerm}
            onChange={(e) => setFilters({...filters, searchTerm: e.target.value, page: 1})}
          />
          
          <select
            value={filters.status}
            onChange={(e) => setFilters({...filters, status: e.target.value, page: 1})}
          >
            <option value="">Todos los estados</option>
            <option value="Pending">Pendiente</option>
            <option value="Resolved">Resuelta</option>
          </select>

          <input
            type="date"
            placeholder="Fecha inicio"
            value={filters.startDate}
            onChange={(e) => setFilters({...filters, startDate: e.target.value, page: 1})}
          />

          <input
            type="date"
            placeholder="Fecha fin"
            value={filters.endDate}
            onChange={(e) => setFilters({...filters, endDate: e.target.value, page: 1})}
          />

          <select
            value={filters.sortBy}
            onChange={(e) => setFilters({...filters, sortBy: e.target.value, page: 1})}
          >
            <option value="createdAt">Fecha de creación</option>
            <option value="status">Estado</option>
            <option value="reason">Razón</option>
          </select>

          <select
            value={filters.sortDirection}
            onChange={(e) => setFilters({...filters, sortDirection: e.target.value, page: 1})}
          >
            <option value="desc">Descendente</option>
            <option value="asc">Ascendente</option>
          </select>
        </div>
      </div>

      {/* Lista de Disputas */}
      <div className="disputes-list">
        {loading ? (
          <div className="loading">Cargando disputas...</div>
        ) : (
          disputes.map(dispute => (
            <DisputeCard 
              key={dispute.id} 
              dispute={dispute} 
              onResolve={loadDisputes}
            />
          ))
        )}
      </div>

      {/* Paginación */}
      <div className="pagination">
        <button 
          disabled={!pagination.hasPrevious}
          onClick={() => setFilters({...filters, page: filters.page - 1})}
        >
          ← Anterior
        </button>
        <span>
          Página {pagination.currentPage} de {pagination.totalPages}
        </span>
        <button 
          disabled={!pagination.hasNext}
          onClick={() => setFilters({...filters, page: filters.page + 1})}
        >
          Siguiente →
        </button>
      </div>
    </div>
  );
}
```

### **2. Componente de Tarjeta de Disputa**

```jsx
function DisputeCard({ dispute, onResolve }) {
  const [showResolveModal, setShowResolveModal] = useState(false);
  const [showSearchDetails, setShowSearchDetails] = useState(false);

  const resolveDispute = async (resolutionData) => {
    try {
      const response = await fetch(`/api/dispute/${dispute.id}/resolve`, {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${localStorage.getItem('token')}`
        },
        body: JSON.stringify(resolutionData)
      });

      if (response.ok) {
        setShowResolveModal(false);
        onResolve(); // Recargar la lista
        alert('Disputa resuelta exitosamente');
      }
    } catch (error) {
      console.error('Error resolving dispute:', error);
      alert('Error al resolver la disputa');
    }
  };

  return (
    <div className={`dispute-card ${dispute.status.toLowerCase()}`}>
      <div className="dispute-header">
        <div className="dispute-id">#{dispute.id}</div>
        <div className={`dispute-status ${dispute.status.toLowerCase()}`}>
          {dispute.statusTranslated}
        </div>
        <div className="dispute-date">
          {new Date(dispute.createdAt).toLocaleDateString()}
        </div>
      </div>

      <div className="dispute-content">
        <div className="dispute-reason">
          <strong>Razón:</strong> {dispute.reason}
        </div>

        <div className="dispute-participants">
          <div className="participant">
            <strong>Reportado por:</strong> {dispute.reporter.name} ({dispute.reporter.email})
          </div>
          <div className="participant">
            <strong>Cliente:</strong> {dispute.client.name} ({dispute.client.email})
          </div>
          {dispute.expert && (
            <div className="participant">
              <strong>Experto:</strong> {dispute.expert.name} ({dispute.expert.email})
            </div>
          )}
        </div>

        <div className="dispute-search-info">
          <strong>Búsqueda:</strong> {dispute.search.title}
          <br />
          <strong>Monto:</strong> €{dispute.searchHire.amount}
        </div>
      </div>

      <div className="dispute-actions">
        <button 
          className="btn-secondary"
          onClick={() => setShowSearchDetails(true)}
        >
          📋 Ver Búsqueda Completa
        </button>
        
        {dispute.status === 'Pending' && (
          <button 
            className="btn-primary"
            onClick={() => setShowResolveModal(true)}
          >
            ✅ Resolver Disputa
          </button>
        )}
      </div>

      {/* Modal de Resolución */}
      {showResolveModal && (
        <ResolveDisputeModal
          dispute={dispute}
          onResolve={resolveDispute}
          onClose={() => setShowResolveModal(false)}
        />
      )}

      {/* Modal de Detalles de Búsqueda */}
      {showSearchDetails && (
        <SearchDetailsModal
          disputeId={dispute.id}
          onClose={() => setShowSearchDetails(false)}
        />
      )}
    </div>
  );
}
```

### **3. Modal de Resolución de Disputa**

```jsx
function ResolveDisputeModal({ dispute, onResolve, onClose }) {
  const [resolutionComments, setResolutionComments] = useState('');
  const [action, setAction] = useState('refund_client');

  const handleSubmit = (e) => {
    e.preventDefault();
    onResolve({
      resolutionComments,
      action
    });
  };

  return (
    <div className="modal-overlay">
      <div className="modal-content">
        <h2>Resolver Disputa #{dispute.id}</h2>
        
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label>Comentarios de Resolución:</label>
            <textarea
              value={resolutionComments}
              onChange={(e) => setResolutionComments(e.target.value)}
              placeholder="Explica la resolución de la disputa..."
              required
              rows={4}
            />
          </div>

          <div className="form-group">
            <label>Acción a Tomar:</label>
            <select
              value={action}
              onChange={(e) => setAction(e.target.value)}
              required
            >
              <option value="refund_client">Reembolsar al Cliente</option>
              <option value="pay_expert">Pagar al Experto</option>
              <option value="no_action">No Hacer Nada Financiero</option>
            </select>
          </div>

          <div className="modal-actions">
            <button type="button" onClick={onClose} className="btn-secondary">
              Cancelar
            </button>
            <button type="submit" className="btn-primary">
              Resolver Disputa
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
```

### **4. Modal de Detalles de Búsqueda**

```jsx
function SearchDetailsModal({ disputeId, onClose }) {
  const [search, setSearch] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const loadSearchDetails = async () => {
      try {
        const response = await fetch(`/api/dispute/${disputeId}/search`, {
          headers: {
            'Authorization': `Bearer ${localStorage.getItem('token')}`
          }
        });

        if (response.ok) {
          const data = await response.json();
          setSearch(data);
        }
      } catch (error) {
        console.error('Error loading search details:', error);
      } finally {
        setLoading(false);
      }
    };

    loadSearchDetails();
  }, [disputeId]);

  if (loading) return <div className="modal-overlay"><div className="modal-content">Cargando...</div></div>;
  if (!search) return <div className="modal-overlay"><div className="modal-content">Error al cargar</div></div>;

  return (
    <div className="modal-overlay">
      <div className="modal-content large">
        <h2>Detalles de la Búsqueda</h2>
        
        <div className="search-details">
          <div className="detail-section">
            <h3>Información General</h3>
            <p><strong>Título:</strong> {search.title}</p>
            <p><strong>Descripción:</strong> {search.description}</p>
            <p><strong>Usuario:</strong> {search.user.name} ({search.user.email})</p>
            <p><strong>Frecuencia:</strong> {search.frequency} días</p>
            <p><strong>Estado:</strong> {search.isActive ? 'Activa' : 'Inactiva'}</p>
          </div>

          <div className="detail-section">
            <h3>Parámetros de Búsqueda</h3>
            {search.searchParameters.map(param => (
              <div key={param.id} className="search-param">
                <p><strong>Categoría:</strong> {param.category}</p>
                <p><strong>Ubicación:</strong> {param.locationName}</p>
                <p><strong>Rango de Precio:</strong> €{param.minPrice} - €{param.maxPrice}</p>
              </div>
            ))}
          </div>

          <div className="detail-section">
            <h3>Contratación del Servicio</h3>
            <p><strong>Estado:</strong> {search.searchHire.statusTranslated}</p>
            <p><strong>Monto:</strong> €{search.searchHire.amount}</p>
            {search.searchHire.expert && (
              <p><strong>Experto:</strong> {search.searchHire.expert.name} ({search.searchHire.expert.email})</p>
            )}
            <p><strong>Tipo de Servicio:</strong> {search.searchHire.service.serviceTypeName}</p>
            <p><strong>Condiciones:</strong> {search.searchHire.service.conditions}</p>
            <p><strong>Duración:</strong> {search.searchHire.service.durationInHours} horas</p>
          </div>
        </div>

        <div className="modal-actions">
          <button onClick={onClose} className="btn-primary">
            Cerrar
          </button>
        </div>
      </div>
    </div>
  );
}
```

---

## 🎨 **Estilos CSS Sugeridos**

```css
.admin-dispute-panel {
  padding: 20px;
  max-width: 1200px;
  margin: 0 auto;
}

.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 20px;
  margin-bottom: 30px;
}

.stat-card {
  background: white;
  padding: 20px;
  border-radius: 8px;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
  text-align: center;
}

.stat-card.pending { border-left: 4px solid #f39c12; }
.stat-card.resolved { border-left: 4px solid #27ae60; }
.stat-card.this-week { border-left: 4px solid #3498db; }
.stat-card.this-month { border-left: 4px solid #9b59b6; }

.stat-number {
  font-size: 2em;
  font-weight: bold;
  display: block;
  margin-top: 10px;
}

.filters-section {
  background: white;
  padding: 20px;
  border-radius: 8px;
  margin-bottom: 20px;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
}

.filter-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 15px;
  align-items: end;
}

.disputes-list {
  display: flex;
  flex-direction: column;
  gap: 15px;
}

.dispute-card {
  background: white;
  border-radius: 8px;
  padding: 20px;
  box-shadow: 0 2px 4px rgba(0,0,0,0.1);
  border-left: 4px solid #e74c3c;
}

.dispute-card.resolved {
  border-left-color: #27ae60;
}

.dispute-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 15px;
  padding-bottom: 10px;
  border-bottom: 1px solid #ecf0f1;
}

.dispute-status {
  padding: 4px 12px;
  border-radius: 20px;
  font-size: 0.8em;
  font-weight: bold;
  text-transform: uppercase;
}

.dispute-status.pending {
  background: #f39c12;
  color: white;
}

.dispute-status.resolved {
  background: #27ae60;
  color: white;
}

.dispute-content {
  margin-bottom: 20px;
}

.dispute-participants {
  margin: 15px 0;
}

.participant {
  margin: 5px 0;
  font-size: 0.9em;
  color: #7f8c8d;
}

.dispute-actions {
  display: flex;
  gap: 10px;
  justify-content: flex-end;
}

.btn-primary {
  background: #3498db;
  color: white;
  border: none;
  padding: 10px 20px;
  border-radius: 4px;
  cursor: pointer;
}

.btn-secondary {
  background: #95a5a6;
  color: white;
  border: none;
  padding: 10px 20px;
  border-radius: 4px;
  cursor: pointer;
}

.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background: rgba(0,0,0,0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.modal-content {
  background: white;
  padding: 30px;
  border-radius: 8px;
  max-width: 500px;
  width: 90%;
  max-height: 80vh;
  overflow-y: auto;
}

.modal-content.large {
  max-width: 800px;
}

.modal-actions {
  display: flex;
  gap: 10px;
  justify-content: flex-end;
  margin-top: 20px;
}

.pagination {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 20px;
  margin-top: 30px;
}

.search-details {
  display: flex;
  flex-direction: column;
  gap: 20px;
}

.detail-section {
  background: #f8f9fa;
  padding: 15px;
  border-radius: 4px;
}

.search-param {
  background: white;
  padding: 10px;
  margin: 10px 0;
  border-radius: 4px;
  border-left: 3px solid #3498db;
}
```

---

## 📝 **Resumen de Funcionalidades**

✅ **Panel de Admin Completo:**
- Lista paginada de todas las disputas
- Filtros por estado, fecha, usuario, término de búsqueda
- Estadísticas en tiempo real
- Ordenamiento configurable

✅ **Gestión de Disputas:**
- Ver detalles completos de cada disputa
- Resolver disputas con diferentes acciones
- Comentarios de resolución
- Navegación a la búsqueda completa

✅ **Información Completa:**
- Datos del cliente, experto y reportero
- Información de la búsqueda y contratación
- Estados traducidos al español
- Historial de transacciones

✅ **Seguridad:**
- Solo administradores pueden acceder
- Validación de permisos en todos los endpoints
- Transacciones atómicas para resolución

¿Necesitas alguna modificación o funcionalidad adicional?






















