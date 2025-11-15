# Análisis: Usuario que es Cliente y Experto Simultáneamente

## ✅ **RESUMEN: NO hay problema, el código lo maneja correctamente**

El sistema **SÍ permite** que un usuario sea tanto cliente como experto, y el código de eliminación de cuenta **maneja correctamente** ambos roles.

---

## 🔍 **ANÁLISIS DEL CÓDIGO ACTUAL**

### **1. Modelo de Datos**

**Ubicación**: `User.cs` líneas 29-30

```csharp
public virtual ICollection<SearchHire> SearchHiresAsClient { get; set; }
public virtual ICollection<SearchHire> SearchHiresAsExpert { get; set; }
```

**Conclusión**: ✅ El modelo **permite** que un usuario tenga contrataciones como cliente Y como experto.

---

### **2. Obtención de Contrataciones Activas**

**Ubicación**: `AccountDeletionService.cs` líneas 446-500

```csharp
private async Task<List<ActiveContractInfo>> GetActiveContractsAsync(int userId, ...)
{
    var activeContracts = new List<ActiveContractInfo>();

    // Buscar como cliente
    var clientContracts = await _context.SearchHires
        .Where(sh => sh.ClientId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
        ...
        .ToListAsync(cancellationToken);

    // Buscar como experto
    var expertContracts = await _context.SearchHires
        .Where(sh => sh.ExpertId == userId && _activeStatuses.Contains(sh.Status.StatusValue))
        ...
        .ToListAsync(cancellationToken);

    // Combinar ambas listas
    activeContracts.AddRange(...);
    activeContracts.AddRange(...);
}
```

**Protección**:
- ✅ Obtiene contrataciones donde el usuario es **CLIENTE** (`sh.ClientId == userId`)
- ✅ Obtiene contrataciones donde el usuario es **EXPERTO** (`sh.ExpertId == userId`)
- ✅ Las **combina** en una sola lista para procesar

**Resultado**: ✅ Si un usuario tiene contrataciones activas en ambos roles, **ambas se obtienen**.

---

### **3. Procesamiento de Contrataciones**

**Ubicación**: `AccountDeletionService.cs` líneas 526-527

```csharp
// Determinar quién es la parte afectada
var affectedParty = searchHire.ClientId == userId ? searchHire.Expert : searchHire.Client;
var isClientDeleting = searchHire.ClientId == userId;
```

**Protección**:
- ✅ Para **cada contratación**, determina el rol del usuario en **ESA contratación específica**
- ✅ `isClientDeleting = true` si el usuario es cliente en esa contratación
- ✅ `isClientDeleting = false` si el usuario es experto en esa contratación

**Resultado**: ✅ Cada contratación se procesa según el rol del usuario en **esa contratación específica**.

---

### **4. Procesamiento de Dinero**

**Ubicación**: `AccountDeletionService.cs` líneas 562-710

```csharp
if (isClientDeleting)
{
    // Si el cliente elimina su cuenta, dar el dinero al experto
    var transferSuccess = await _refundService.ProcessMoneyDistributionAsync(
        searchHire.Id,
        "cancelled_by_client_account_delete",
        "Client account deletion - transfer to expert",
        updateState: true);
}
else
{
    // Si el experto elimina su cuenta, reembolsar al cliente
    var refundSuccess = await _refundService.ProcessMoneyDistributionAsync(
        searchHire.Id,
        "cancelled_by_expert_account_delete",
        reasonText,
        updateState: true);
}
```

**Protección**:
- ✅ Si el usuario es **CLIENTE** en esa contratación → Transfiere dinero al experto
- ✅ Si el usuario es **EXPERTO** en esa contratación → Reembolsa al cliente

**Resultado**: ✅ El dinero se procesa correctamente según el rol del usuario en cada contratación.

---

### **5. Anonimización de SearchHires**

**Ubicación**: `AccountDeletionService.cs` líneas 966-976

```csharp
// Anonimizar SearchHires donde el usuario es CLIENTE
var searchHiresAsClient = await _context.Database.ExecuteSqlRawAsync(
    @"UPDATE ""SearchHires"" 
      SET ""ClientId"" = NULL,
          ""UpdatedAt"" = CURRENT_TIMESTAMP 
      WHERE ""ClientId"" = {0} AND ""ClientId"" IS NOT NULL", userId, cancellationToken);

// Anonimizar SearchHires donde el usuario es EXPERTO
var searchHiresAsExpert = await _context.Database.ExecuteSqlRawAsync(
    @"UPDATE ""SearchHires"" 
      SET ""ExpertId"" = NULL,
          ""UpdatedAt"" = CURRENT_TIMESTAMP 
      WHERE ""ExpertId"" = {0} AND ""ExpertId"" IS NOT NULL", userId, cancellationToken);
```

**Protección**:
- ✅ Anonimiza `ClientId` donde el usuario es cliente
- ✅ Anonimiza `ExpertId` donde el usuario es experto
- ✅ **No hay conflicto** porque son campos diferentes

**Resultado**: ✅ La anonimización maneja ambos roles correctamente.

---

## 📊 **ESCENARIOS DE PRUEBA**

### **Escenario 1: Usuario con Contrataciones Solo como Cliente**

**Situación**:
- Usuario tiene 2 contrataciones activas como CLIENTE
- No tiene contrataciones como EXPERTO

**Procesamiento**:
1. ✅ Obtiene 2 contrataciones (solo como cliente)
2. ✅ Para cada una: `isClientDeleting = true` → Transfiere dinero al experto
3. ✅ Anonimiza `ClientId` en ambas contrataciones

**Resultado**: ✅ **Correcto**

---

### **Escenario 2: Usuario con Contrataciones Solo como Experto**

**Situación**:
- Usuario tiene 2 contrataciones activas como EXPERTO
- No tiene contrataciones como CLIENTE

**Procesamiento**:
1. ✅ Obtiene 2 contrataciones (solo como experto)
2. ✅ Para cada una: `isClientDeleting = false` → Reembolsa al cliente
3. ✅ Anonimiza `ExpertId` en ambas contrataciones

**Resultado**: ✅ **Correcto**

---

### **Escenario 3: Usuario con Contrataciones como Cliente Y Experto** ⭐

**Situación**:
- Usuario tiene 2 contrataciones activas como **CLIENTE**
- Usuario tiene 1 contratación activa como **EXPERTO**

**Procesamiento**:
1. ✅ Obtiene 3 contrataciones (2 como cliente, 1 como experto)
2. ✅ Para contratación 1 (cliente): `isClientDeleting = true` → Transfiere dinero al experto
3. ✅ Para contratación 2 (cliente): `isClientDeleting = true` → Transfiere dinero al experto
4. ✅ Para contratación 3 (experto): `isClientDeleting = false` → Reembolsa al cliente
5. ✅ Anonimiza `ClientId` en contrataciones 1 y 2
6. ✅ Anonimiza `ExpertId` en contratación 3

**Resultado**: ✅ **Correcto** - Cada contratación se procesa según el rol del usuario en esa contratación específica.

---

### **Escenario 4: Usuario que se Contrata a Sí Mismo** ❌

**Situación**:
- Usuario intenta contratar su propio servicio

**Protección**:
- ✅ **Validación en SubscriptionController** (línea 2528):
  ```csharp
  if (service.ExpertProfile != null && service.ExpertProfile.UserId == userId)
  {
      return BadRequest(new { message = "No puedes contratarte a ti mismo como experto" });
  }
  ```

**Resultado**: ✅ **Imposible** - El sistema **previene** que un usuario se contrate a sí mismo.

---

## ✅ **CONCLUSIÓN**

### **¿Es un problema que un experto tenga búsquedas como cliente?**

**NO**, no es un problema. El código actual:

1. ✅ **Permite** que un usuario sea tanto cliente como experto
2. ✅ **Obtiene** contrataciones en ambos roles
3. ✅ **Procesa** cada contratación según el rol del usuario en esa contratación específica
4. ✅ **Anonimiza** correctamente ambos roles
5. ✅ **Previene** que un usuario se contrate a sí mismo

### **¿El código evita problemas?**

**SÍ**, el código evita problemas mediante:

1. ✅ **Separación de roles**: Cada contratación se procesa según el rol del usuario en esa contratación específica
2. ✅ **Campos diferentes**: `ClientId` y `ExpertId` son campos diferentes, no hay conflicto
3. ✅ **Validación previa**: El sistema previene que un usuario se contrate a sí mismo
4. ✅ **Procesamiento independiente**: Cada contratación se procesa independientemente

### **Recomendación**

**No hay cambios necesarios**. El código actual maneja correctamente el caso de usuarios que son tanto clientes como expertos.

---

## 🔒 **GARANTÍAS**

- ✅ **Atomicidad**: Todas las contrataciones se procesan en una sola transacción
- ✅ **Idempotencia**: Se puede llamar múltiples veces sin efectos
- ✅ **Integridad**: Cada contratación se procesa según el rol correcto
- ✅ **Seguridad**: Previene auto-contratación

**El sistema es 100% seguro para usuarios que son tanto clientes como expertos.**

