using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using newApi.Services;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ServiceTypeController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IAuthorizationServices _authService;
        private readonly ILogger<ServiceTypeController> _logger;

        public ServiceTypeController(AppDbContext context, IAuthorizationServices authService, ILogger<ServiceTypeController> logger)
        {
            _context = context;
            _authService = authService;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene todos los tipos de servicio activos para el frontend
        /// </summary>
        /// <returns>Lista de tipos de servicio activos</returns>
        [HttpGet]
        [AllowAnonymous] // ✅ PÚBLICO: Permitir acceso sin autenticación para explorar servicios
        public async Task<IActionResult> GetServiceTypes()
        {
            try
            {
                // ✅ Corregido: Usar AsNoTracking() y proyección directa sin Include() para evitar problemas con multiplexing
                var serviceTypes = await _context.ServiceTypes
                    .AsNoTracking() // ✅ Evitar tracking innecesario y problemas con multiplexing
                    .Where(st => st.IsActive)
                    .OrderBy(st => st.Position) // Ordenar por posición personalizada
                    .ThenBy(st => st.Name) // Luego alfabéticamente como fallback
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                        Position = st.Position,
                        IsActive = st.IsActive,
                        RequiresAppointment = st.RequiresAppointment,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .ToListAsync();
                return Ok(new 
                { 
                    success = true,
                    data = serviceTypes,
                    count = serviceTypes.Count,
                    message = "Service types retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service types");
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error al obtener los tipos de servicio",
                    errorCode = "SERVICE_TYPES_RETRIEVE_ERROR"
                });
            }
        }

        /// <summary>
        /// Obtiene todos los tipos de servicio activos (endpoint público sin autenticación)
        /// </summary>
        /// <returns>Lista de tipos de servicio activos</returns>
        [HttpGet("public")]
        [AllowAnonymous]
        public async Task<IActionResult> GetServiceTypesPublic()
        {
            var startTime = DateTime.UtcNow;
            var requestId = Guid.NewGuid().ToString("N")[..8];
            
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] ========================================");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] 🏠 HOMEPAGE ENDPOINT: /api/ServiceType/public");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT] 📥 GetServiceTypesPublic INICIADO - RequestId: {requestId}");
            _logger.LogInformation($"[HOMEPAGE-ENDPOINT]    Timestamp: {startTime:yyyy-MM-dd HH:mm:ss.fff}");
            
            try
            {
                // ✅ Corregido: Usar AsNoTracking() y proyección directa sin Include() para evitar problemas con multiplexing
                _logger.LogInformation($"[ENDPOINT] 🔍 GetServiceTypesPublic - Ejecutando consulta a base de datos...");
                
                // ✅ LOG ESTADO CONEXIÓN: Verificar estado de la conexión antes de la consulta
                var connectionStateStartTime = DateTime.UtcNow;
                try
                {
                    var canConnectBefore = await _context.Database.CanConnectAsync();
                    var connectionStateDuration = (DateTime.UtcNow - connectionStateStartTime).TotalMilliseconds;
                    _logger.LogInformation($"[ENDPOINT]    Estado conexión ANTES de query: CanConnect={canConnectBefore}, Duración verificación: {connectionStateDuration:F2}ms");
                }
                catch (Exception connEx)
                {
                    _logger.LogWarning($"[ENDPOINT]    ⚠️ Error verificando conexión antes de query: {connEx.Message}");
                }
                
                // ✅ LOG SQL QUERY: Obtener la query SQL generada para diagnóstico
                try
                {
                    var query = _context.ServiceTypes
                        .AsNoTracking()
                        .Where(st => st.IsActive)
                        .OrderBy(st => st.Position)
                        .ThenBy(st => st.Name)
                        .Select(st => new 
                        {
                            Id = st.Id,
                            Name = st.Name,
                            Description = st.Description,
                            ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                            ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                            Position = st.Position,
                            RequiresAppointment = st.RequiresAppointment
                        });
                    var sqlQuery = query.ToQueryString();
                    _logger.LogInformation($"[ENDPOINT]    SQL Query generada (primeros 500 chars): {sqlQuery.Substring(0, Math.Min(500, sqlQuery.Length))}...");
                }
                catch (Exception sqlEx)
                {
                    _logger.LogWarning($"[ENDPOINT]    ⚠️ No se pudo obtener SQL query: {sqlEx.Message}");
                }
                
                var queryStartTime = DateTime.UtcNow;
                
                // ✅ TIMEOUT: Agregar timeout de 10 segundos para evitar bloqueos
                // ✅ FIX CRÍTICO: Usar try-finally para asegurar que CancellationTokenSource se disponga correctamente
                // incluso si hay ObjectDisposedException durante la cancelación
                CancellationTokenSource? queryCts = null;
                try
                {
                    queryCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    
                    // ✅ LOG TIMEOUT: Registrar cuando se crea el token de cancelación
                    _logger.LogInformation($"[ENDPOINT]    ⏱️ Timeout configurado: 10 segundos");
                    _logger.LogInformation($"[ENDPOINT]    Token de cancelación creado: {queryCts.Token.CanBeCanceled}");
                }
                catch (Exception ctsEx)
                {
                    _logger.LogError(ctsEx, $"[ENDPOINT] ❌ Error creando CancellationTokenSource: {ctsEx.Message}");
                    // Continuar sin timeout si falla la creación
                    queryCts = null;
                }
                
                var serviceTypes = new List<dynamic>();
                double queryDuration = 0; // ✅ Declarar fuera del try para que esté disponible después
                try
                {
                    if (queryCts == null)
                    {
                        _logger.LogWarning($"[ENDPOINT] ⚠️ Continuando sin timeout (CancellationTokenSource no disponible)");
                    }
                    _logger.LogInformation($"[ENDPOINT]    ⏱️ Iniciando ToListAsync con timeout de 10 segundos...");
                    _logger.LogInformation($"[ENDPOINT]    Timestamp inicio: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    _logger.LogInformation($"[ENDPOINT]    Token antes de ToListAsync: IsCancellationRequested={queryCts.Token.IsCancellationRequested}");
                    
                    // ✅ CRÍTICO PRODUCCIÓN: El CommandTimeout de la connection string (120s) tiene prioridad sobre CancellationToken
                    // ✅ SOLUCIÓN: Establecer timeout temporalmente en el DbContext antes de la query
                    var originalTimeout = _context.Database.GetCommandTimeout();
                    _context.Database.SetCommandTimeout(10); // ✅ Forzar timeout de 10 segundos para esta query
                    
                    try
                    {
                        // ✅ FIX CRÍTICO: Usar token solo si está disponible, sino usar CancellationToken.None
                        var cancellationToken = queryCts?.Token ?? CancellationToken.None;
                        
                        serviceTypes = (await _context.ServiceTypes
                            .AsNoTracking() // ✅ Evitar tracking innecesario y problemas con multiplexing
                            .Where(st => st.IsActive)
                            .OrderBy(st => st.Position) // Ordenar por posición personalizada
                            .ThenBy(st => st.Name) // Luego alfabéticamente como fallback
                            .Select(st => new 
                            {
                                Id = st.Id,
                                Name = st.Name,
                                Description = st.Description,
                                ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                                ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                                Position = st.Position,
                                RequiresAppointment = st.RequiresAppointment
                            })
                            .ToListAsync(cancellationToken)).Cast<dynamic>().ToList();
                    }
                    finally
                    {
                        // Restaurar timeout original
                        _context.Database.SetCommandTimeout(originalTimeout);
                    }
                    
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    
                    _logger.LogInformation($"[ENDPOINT]    Timestamp fin: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    _logger.LogInformation($"[ENDPOINT] ✅ GetServiceTypesPublic - Consulta completada: {serviceTypes.Count} service types, Duración: {queryDuration:F2}ms");
                    
                    // ✅ LOG ESTADO CONEXIÓN: Verificar estado de la conexión después de la consulta
                    try
                    {
                        var canConnectAfter = await _context.Database.CanConnectAsync();
                        _logger.LogInformation($"[ENDPOINT]    Estado conexión DESPUÉS de query: CanConnect={canConnectAfter}");
                    }
                    catch (Exception connEx)
                    {
                        _logger.LogWarning($"[ENDPOINT]    ⚠️ Error verificando conexión después de query: {connEx.Message}");
                    }
                }
                catch (OperationCanceledException timeoutEx)
                {
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogError(timeoutEx, $"[ENDPOINT] ❌ GetServiceTypesPublic - TIMEOUT en consulta después de {queryDuration:F2}ms");
                    _logger.LogError($"[ENDPOINT]    La consulta excedió el timeout de 10 segundos");
                    _logger.LogError($"[ENDPOINT]    Timestamp timeout: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    _logger.LogError($"[ENDPOINT]    Exception Type: {timeoutEx.GetType().Name}");
                    _logger.LogError($"[ENDPOINT]    Exception Message: {timeoutEx.Message}");
                    if (queryCts != null)
                    {
                        try
                        {
                            _logger.LogError($"[ENDPOINT]    Token IsCancellationRequested: {queryCts.Token.IsCancellationRequested}");
                        }
                        catch (ObjectDisposedException)
                        {
                            _logger.LogError($"[ENDPOINT]    Token ya fue disposed (normal después de timeout)");
                        }
                    }
                    _logger.LogError($"[ENDPOINT]    Timestamp timeout: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    
                    // ✅ LOG ESTADO CONEXIÓN: Verificar estado de la conexión después del timeout
                    try
                    {
                        var canConnectAfterTimeout = await _context.Database.CanConnectAsync();
                        _logger.LogError($"[ENDPOINT]    Estado conexión DESPUÉS de timeout: CanConnect={canConnectAfterTimeout}");
                    }
                    catch (Exception connEx)
                    {
                        _logger.LogError($"[ENDPOINT]    ❌ Error verificando conexión después de timeout: {connEx.Message}");
                    }
                    
                    // ✅ CRÍTICO: Asegurar headers CORS y Content-Type ANTES de devolver StatusCode
                    Response.Headers["Access-Control-Allow-Origin"] = Request.Headers["Origin"].ToString();
                    Response.Headers["Access-Control-Allow-Credentials"] = "true";
                    Response.ContentType = "application/json";
                    
                    return StatusCode(408, new { 
                        success = false,
                        message = "Database query timeout. Please try again.",
                        error = "QUERY_TIMEOUT",
                        details = "The database query took longer than 10 seconds to complete"
                    });
                }
                catch (AggregateException aggEx) when (aggEx.InnerException is ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Manejar AggregateException con ObjectDisposedException
                    // Esto ocurre cuando el CancellationTokenSource intenta cancelar una operación
                    // mientras la conexión ya está siendo disposed
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogWarning(aggEx, $"[ENDPOINT] ⚠️ GetServiceTypesPublic - ObjectDisposedException durante cancelación después de {queryDuration:F2}ms");
                    _logger.LogWarning($"[ENDPOINT]    Esto es normal cuando la conexión se cierra durante un timeout");
                    _logger.LogWarning($"[ENDPOINT]    Exception Type: {aggEx.GetType().Name}");
                    _logger.LogWarning($"[ENDPOINT]    Inner Exception: {disposedEx.Message}");
                    
                    // ✅ CRÍTICO: Asegurar headers CORS y Content-Type ANTES de devolver StatusCode
                    Response.Headers["Access-Control-Allow-Origin"] = Request.Headers["Origin"].ToString();
                    Response.Headers["Access-Control-Allow-Credentials"] = "true";
                    Response.ContentType = "application/json";
                    
                    return StatusCode(408, new { 
                        success = false,
                        message = "Database query timeout. Please try again.",
                        error = "QUERY_TIMEOUT",
                        details = "The database query took longer than 10 seconds to complete"
                    });
                }
                catch (ObjectDisposedException disposedEx)
                {
                    // ✅ FIX CRÍTICO: Manejar ObjectDisposedException directamente
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogWarning(disposedEx, $"[ENDPOINT] ⚠️ GetServiceTypesPublic - ObjectDisposedException después de {queryDuration:F2}ms");
                    _logger.LogWarning($"[ENDPOINT]    Esto puede ocurrir cuando la conexión se cierra durante un timeout");
                    
                    // ✅ CRÍTICO: Asegurar headers CORS y Content-Type ANTES de devolver StatusCode
                    Response.Headers["Access-Control-Allow-Origin"] = Request.Headers["Origin"].ToString();
                    Response.Headers["Access-Control-Allow-Credentials"] = "true";
                    Response.ContentType = "application/json";
                    
                    return StatusCode(408, new { 
                        success = false,
                        message = "Database query timeout. Please try again.",
                        error = "QUERY_TIMEOUT",
                        details = "The database query took longer than 10 seconds to complete"
                    });
                }
                catch (Exception queryEx)
                {
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogError($"[ENDPOINT] ❌ GetServiceTypesPublic - ERROR en consulta después de {queryDuration:F2}ms");
                    _logger.LogError($"[ENDPOINT]    Exception Type: {queryEx.GetType().Name}");
                    _logger.LogError($"[ENDPOINT]    Exception Message: {queryEx.Message}");
                    _logger.LogError($"[ENDPOINT]    Inner Exception: {queryEx.InnerException?.Message ?? "None"}");
                    _logger.LogError($"[ENDPOINT]    StackTrace: {queryEx.StackTrace}");
                    _logger.LogError($"[ENDPOINT]    Timestamp error: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    
                    // ✅ LOG ESTADO CONEXIÓN: Verificar estado de la conexión después del error
                    try
                    {
                        var canConnectAfterError = await _context.Database.CanConnectAsync();
                        _logger.LogError($"[ENDPOINT]    Estado conexión DESPUÉS de error: CanConnect={canConnectAfterError}");
                    }
                    catch (Exception connEx)
                    {
                        _logger.LogError($"[ENDPOINT]    ❌ Error verificando conexión después de error: {connEx.Message}");
                    }
                    
                    throw; // Re-lanzar para que se maneje en el catch general
                }
                finally
                {
                    // ✅ FIX CRÍTICO: Asegurar que CancellationTokenSource se disponga correctamente
                    // incluso si hay ObjectDisposedException durante la cancelación
                    if (queryCts != null)
                    {
                        try
                        {
                            // Cancelar primero para evitar que el timer siga ejecutándose
                            if (!queryCts.Token.IsCancellationRequested)
                            {
                                queryCts.Cancel();
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // Ya está disposed, ignorar
                        }
                        catch (Exception disposeEx)
                        {
                            _logger.LogWarning(disposeEx, $"[ENDPOINT] ⚠️ Error cancelando CancellationTokenSource: {disposeEx.Message}");
                        }
                        
                        try
                        {
                            queryCts.Dispose();
                        }
                        catch (ObjectDisposedException)
                        {
                            // Ya está disposed, ignorar
                        }
                        catch (Exception disposeEx)
                        {
                            _logger.LogWarning(disposeEx, $"[ENDPOINT] ⚠️ Error disponiendo CancellationTokenSource: {disposeEx.Message}");
                        }
                    }
                }

                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation($"[ENDPOINT] ✅ GetServiceTypesPublic COMPLETADO - RequestId: {requestId}");
                _logger.LogInformation($"[ENDPOINT]    Total service types: {serviceTypes.Count}");
                _logger.LogInformation($"[ENDPOINT]    Duración total: {totalDuration:F2}ms");
                
                var response = new 
                { 
                    success = true,
                    data = serviceTypes,
                    count = serviceTypes.Count,
                    message = "Service types retrieved successfully"
                };
                
                // ✅ LOG JSON: Serializar y loguear el JSON que se está devolviendo
                try
                {
                    var jsonResponse = System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions 
                    { 
                        WriteIndented = false,
                        MaxDepth = 3 // Limitar profundidad para evitar logs muy largos
                    });
                    var jsonLength = jsonResponse.Length;
                    var jsonPreview = jsonLength > 1000 ? jsonResponse.Substring(0, 1000) + "..." : jsonResponse;
                    _logger.LogInformation($"[ENDPOINT] 📤 JSON RESPONSE - RequestId: {requestId}");
                    _logger.LogInformation($"[ENDPOINT]    JSON Length: {jsonLength} caracteres");
                    _logger.LogInformation($"[ENDPOINT]    JSON Preview (primeros 1000 chars): {jsonPreview}");
                }
                catch (Exception jsonEx)
                {
                    _logger.LogWarning($"[ENDPOINT] ⚠️ Error serializando JSON para log: {jsonEx.Message}");
                }
                
                _logger.LogInformation($"[ENDPOINT] ========================================");
                return Ok(response);
            }
            catch (Exception ex)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogError(ex, $"[HOMEPAGE-ENDPOINT] ❌ GetServiceTypesPublic EXCEPCIÓN - RequestId: {requestId}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    🏠 HOMEPAGE ENDPOINT: /api/ServiceType/public");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Exception Type: {ex.GetType().Name}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Exception Message: {ex.Message}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Inner Exception: {ex.InnerException?.Message ?? "None"}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    StackTrace: {ex.StackTrace}");
                _logger.LogError($"[HOMEPAGE-ENDPOINT]    Duración antes del error: {totalDuration:F2}ms");
                _logger.LogError($"[HOMEPAGE-ENDPOINT] ========================================");
                
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error al obtener los tipos de servicio",
                    errorCode = "SERVICE_TYPES_RETRIEVE_ERROR"
                });
            }
        }

        /// <summary>
        /// Obtiene un tipo de servicio específico por ID
        /// </summary>
        /// <param name="id">ID del tipo de servicio</param>
        /// <returns>Tipo de servicio específico</returns>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetServiceType(int id)
        {
            try
            {
                var serviceType = await _context.ServiceTypes
                    .Include(st => st.ServiceTypeCategory)
                    .Where(st => st.Id == id && st.IsActive)
                    .Select(st => new ServiceTypeDto
                    {
                        Id = st.Id,
                        Name = st.Name,
                        Description = st.Description,
                        ServiceTypeCategoryId = st.ServiceTypeCategoryId,
                        ServiceTypeCategoryName = st.ServiceTypeCategory != null ? st.ServiceTypeCategory.Name : null,
                        Position = st.Position,
                        IsActive = st.IsActive,
                        RequiresAppointment = st.RequiresAppointment,
                        CreatedAt = st.CreatedAt,
                        UpdatedAt = st.UpdatedAt
                    })
                    .FirstOrDefaultAsync();

                if (serviceType == null)
                {
                    return NotFound(new 
                    { 
                        success = false,
                        message = "Service type not found" 
                    });
                }
                return Ok(new 
                { 
                    success = true,
                    data = serviceType,
                    message = "Service type retrieved successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving service type {Id}", id);
                return StatusCode(500, new
                {
                    success = false,
                    message = "Error al obtener el tipo de servicio",
                    errorCode = "SERVICE_TYPE_RETRIEVE_ERROR"
                });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CreateServiceType([FromBody] ServiceTypeDto createDto)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                if (string.IsNullOrWhiteSpace(createDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                var serviceType = new ServiceType
                {
                    Name = createDto.Name,
                    Description = createDto.Description ?? "",
                    Position = createDto.Position,
                    IsActive = createDto.IsActive,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _context.ServiceTypes.AddAsync(serviceType);
                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Description = serviceType.Description,
                    Position = serviceType.Position,
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return CreatedAtAction(nameof(GetServiceType), new { id = serviceType.Id }, resultDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating service type");
                return StatusCode(500, new { message = "Error al crear el tipo de servicio", errorCode = "SERVICE_TYPE_CREATE_ERROR" });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateServiceType(int id, [FromBody] ServiceTypeDto updateDto)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                if (id != updateDto.Id)
                {
                    return BadRequest(new { message = "ID mismatch" });
                }

                var serviceType = await _context.ServiceTypes.FindAsync(id);
                if (serviceType == null)
                {
                    return NotFound(new { message = "Service type not found" });
                }

                if (string.IsNullOrWhiteSpace(updateDto.Name))
                {
                    return BadRequest(new { message = "Name is required" });
                }

                serviceType.Name = updateDto.Name;
                serviceType.Description = updateDto.Description ?? "";
                serviceType.Position = updateDto.Position;
                serviceType.IsActive = updateDto.IsActive;
                serviceType.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var resultDto = new ServiceTypeDto
                {
                    Id = serviceType.Id,
                    Name = serviceType.Name,
                    Description = serviceType.Description,
                    Position = serviceType.Position,
                    IsActive = serviceType.IsActive,
                    CreatedAt = serviceType.CreatedAt,
                    UpdatedAt = serviceType.UpdatedAt
                };

                return Ok(resultDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating service type {Id}", id);
                return StatusCode(500, new { message = "Error al actualizar el tipo de servicio", errorCode = "SERVICE_TYPE_UPDATE_ERROR" });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteServiceType(int id)
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
                var serviceType = await _context.ServiceTypes.FindAsync(id);
                if (serviceType == null)
                {
                    return NotFound(new { message = "Service type not found" });
                }

                var hasDependencies = await _context.SearchParameters.AnyAsync(sp => sp.ServiceTypeId == id) ||
                                     await _context.SearchServices.AnyAsync(ss => ss.ServiceTypeId == id);
                if (hasDependencies)
                {
                    return BadRequest(new { message = "Cannot delete service type with associated search parameters or services" });
                }

                _context.ServiceTypes.Remove(serviceType);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Service type deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting service type {Id}", id);
                return StatusCode(500, new { message = "Error al eliminar el tipo de servicio", errorCode = "SERVICE_TYPE_DELETE_ERROR" });
            }
        }
    }
    public class ServiceTypeDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int? ServiceTypeCategoryId { get; set; }
        public string? ServiceTypeCategoryName { get; set; }
        public int Position { get; set; }
        public bool IsActive { get; set; }
        public bool RequiresAppointment { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}