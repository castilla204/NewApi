using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using Npgsql;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CategoriesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<CategoriesController> _logger;

        public CategoriesController(AppDbContext context, IMapper mapper, ILogger<CategoriesController> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// Obtiene solo las categorías padre (sin subcategorías) para seleccionar al crear subcategorías
        /// IMPORTANTE: Esta ruta debe estar antes de las rutas con parámetros para evitar conflictos
        /// </summary>
        [HttpGet("parents")]
        public async Task<IActionResult> GetParentCategories()
        {
            try
            {
                // ✅ CORRECCIÓN: Verificar conexión antes de consultar
                if (!await _context.Database.CanConnectAsync())
                {
                    return StatusCode(503, new { 
                        success = false,
                        message = "Database connection unavailable. Please check your SSH tunnel.",
                        error = "DATABASE_CONNECTION_LOST"
                    });
                }

                var parentCategories = await _context.Categories
                    .AsNoTracking() // ✅ MEJORA: No tracking para mejor rendimiento
                    .Where(c => c.IsActive && c.ParentId == null)
                    .Include(c => c.Subcategories)
                    .OrderBy(c => c.Name)
                    .ToListAsync();

                var result = parentCategories.Select(c => new ParentCategoryDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    IsActive = c.IsActive,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    SubcategoriesCount = c.Subcategories != null ? c.Subcategories.Count(sc => sc.IsActive) : 0
                }).ToList();
                return Ok(new
                {
                    success = true,
                    data = result,
                    count = result.Count,
                    message = "Parent categories retrieved successfully"
                });
            }
            catch (Npgsql.NpgsqlException npgsqlEx)
            {
                // ✅ CORRECCIÓN: Manejo específico de errores de PostgreSQL
                // SqlState puede no estar disponible en NpgsqlException, intentar obtenerlo del PostgresException interno
                var sqlState = (npgsqlEx.InnerException as Npgsql.PostgresException)?.SqlState ?? "UNKNOWN";
                return StatusCode(503, new { 
                    success = false,
                    message = "Database connection error. Please check your SSH tunnel.",
                    error = "DATABASE_CONNECTION_ERROR",
                    details = npgsqlEx.Message,
                    sqlState = sqlState
                });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                // ✅ CORRECCIÓN: Manejo de errores de Entity Framework
                return StatusCode(503, new { 
                    success = false,
                    message = "Database error occurred.",
                    error = "DATABASE_ERROR",
                    details = dbEx.InnerException?.Message ?? dbEx.Message
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { 
                    success = false,
                    message = "Failed to retrieve parent categories",
                    error = ex.Message,
                    errorType = ex.GetType().Name
                });
            }
        }

        [HttpGet]
        [AllowAnonymous] // ✅ PÚBLICO: Permitir acceso sin autenticación para explorar categorías
        public async Task<IActionResult> GetCategories()
        {
            var startTime = DateTime.UtcNow;
            var requestId = Guid.NewGuid().ToString("N")[..8];
            
            _logger.LogInformation($"[ENDPOINT] ========================================");
            _logger.LogInformation($"[ENDPOINT] 📥 GetCategories INICIADO - RequestId: {requestId}");
            
            try
            {
                // ✅ CORRECCIÓN: Verificar conexión antes de consultar
                _logger.LogInformation($"[ENDPOINT] 🔍 GetCategories - Verificando conexión a base de datos...");
                var canConnectStartTime = DateTime.UtcNow;
                var canConnect = await _context.Database.CanConnectAsync();
                var canConnectDuration = (DateTime.UtcNow - canConnectStartTime).TotalMilliseconds;
                _logger.LogInformation($"[ENDPOINT] ✅ GetCategories - CanConnect: {canConnect}, Duración: {canConnectDuration:F2}ms");
                
                if (!canConnect)
                {
                    _logger.LogError($"[ENDPOINT] ❌ GetCategories - Database connection unavailable");
                    return StatusCode(503, new { 
                        message = "Database connection unavailable. Please check your SSH tunnel.",
                        error = "DATABASE_CONNECTION_LOST"
                    });
                }

                // ✅ CORRECCIÓN: Consulta más robusta - usar Select para evitar problemas de referencia circular
                // y cargar solo las subcategorías activas directamente en la consulta
                _logger.LogInformation($"[ENDPOINT] 🔍 GetCategories - Ejecutando consulta a base de datos...");
                _logger.LogInformation($"[ENDPOINT]    Query: Categories.Where(IsActive).Select(Category + ActiveSubcategories)");
                
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
                    var query = _context.Categories
                        .AsNoTracking()
                        .Where(c => c.IsActive)
                        .Select(c => new
                        {
                            Category = c,
                            ActiveSubcategories = c.Subcategories != null 
                                ? c.Subcategories.Where(sc => sc.IsActive && sc != null).ToList()
                                : new List<Category>()
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
                using var queryCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                
                var categoryData = new List<dynamic>();
                double queryDuration = 0; // ✅ Declarar fuera del try para que esté disponible después
                try
                {
                    _logger.LogInformation($"[ENDPOINT]    ⏱️ Iniciando ToListAsync con timeout de 10 segundos...");
                    _logger.LogInformation($"[ENDPOINT]    Timestamp inicio: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    
                    categoryData = (await _context.Categories
                        .AsNoTracking() // ✅ MEJORA: No tracking para mejor rendimiento y evitar problemas de conexión
                        .Where(c => c.IsActive)
                        .Select(c => new
                        {
                            Category = c,
                            ActiveSubcategories = c.Subcategories != null 
                                ? c.Subcategories.Where(sc => sc.IsActive && sc != null).ToList()
                                : new List<Category>()
                        })
                        .ToListAsync(queryCts.Token)).Cast<dynamic>().ToList();
                    
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogInformation($"[ENDPOINT]    Timestamp fin: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
                    _logger.LogInformation($"[ENDPOINT] ✅ GetCategories - Consulta completada: {categoryData.Count} categorías, Duración: {queryDuration:F2}ms");
                    
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
                catch (OperationCanceledException)
                {
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogError($"[ENDPOINT] ❌ GetCategories - TIMEOUT en consulta después de {queryDuration:F2}ms");
                    _logger.LogError($"[ENDPOINT]    La consulta excedió el timeout de 10 segundos");
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
                    
                    return StatusCode(408, new { 
                        message = "Database query timeout. Please try again.",
                        error = "QUERY_TIMEOUT",
                        details = "The database query took longer than 10 seconds to complete"
                    });
                }
                catch (Exception queryEx)
                {
                    queryDuration = (DateTime.UtcNow - queryStartTime).TotalMilliseconds;
                    _logger.LogError($"[ENDPOINT] ❌ GetCategories - ERROR en consulta después de {queryDuration:F2}ms");
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

                // Construir el resultado final mapeando a DTO
                _logger.LogInformation($"[ENDPOINT] 🔍 GetCategories - Mapeando a DTOs...");
                var mappingStartTime = DateTime.UtcNow;
                var categoryDtos = categoryData.Select(x => new CategoryWithDetailsDto
                {
                    Id = x.Category.Id,
                    Name = x.Category.Name ?? string.Empty, // ✅ Protección contra null
                    ParentId = x.Category.ParentId,
                    IsActive = x.Category.IsActive,
                    CreatedAt = x.Category.CreatedAt,
                    UpdatedAt = x.Category.UpdatedAt,
                    IsParent = x.Category.ParentId == null,
                    HasSubcategories = x.ActiveSubcategories.Any(),
                    SubcategoriesCount = x.ActiveSubcategories.Count
                }).ToList();
                var mappingDuration = (DateTime.UtcNow - mappingStartTime).TotalMilliseconds;
                
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                _logger.LogInformation($"[ENDPOINT] ✅ GetCategories COMPLETADO - RequestId: {requestId}");
                _logger.LogInformation($"[ENDPOINT]    Total categorías: {categoryDtos.Count}");
                _logger.LogInformation($"[ENDPOINT]    Duración total: {totalDuration:F2}ms (Query: {queryDuration:F2}ms, Mapping: {mappingDuration:F2}ms)");
                
                // ✅ LOG JSON: Serializar y loguear el JSON que se está devolviendo
                try
                {
                    var jsonResponse = System.Text.Json.JsonSerializer.Serialize(categoryDtos, new System.Text.Json.JsonSerializerOptions 
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

                return Ok(categoryDtos);
            }
            catch (Npgsql.NpgsqlException npgsqlEx)
            {
                var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
                var sqlState = (npgsqlEx.InnerException as Npgsql.PostgresException)?.SqlState ?? "UNKNOWN";
                
                _logger.LogError(npgsqlEx, $"[ENDPOINT] ❌ GetCategories NpgsqlException - RequestId: {requestId}");
                _logger.LogError($"[ENDPOINT]    SqlState: {sqlState}");
                _logger.LogError($"[ENDPOINT]    Message: {npgsqlEx.Message}");
                _logger.LogError($"[ENDPOINT]    Duración antes del error: {totalDuration:F2}ms");
                
                // ✅ CORRECCIÓN: Manejo específico de errores de PostgreSQL
                // SqlState puede no estar disponible en NpgsqlException, intentar obtenerlo del PostgresException interno
                return StatusCode(503, new { 
                    message = "Database connection error. Please check your SSH tunnel.",
                    error = "DATABASE_CONNECTION_ERROR",
                    details = npgsqlEx.Message,
                    sqlState = sqlState
                });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
            {
                // ✅ CORRECCIÓN: Manejo de errores de Entity Framework
                return StatusCode(503, new { 
                    message = "Database error occurred.",
                    error = "DATABASE_ERROR",
                    details = dbEx.InnerException?.Message ?? dbEx.Message
                });
            }
            catch (NullReferenceException nullEx)
            {
                // ✅ NUEVO: Manejo específico de NullReferenceException
                return StatusCode(500, new { 
                    message = "Failed to retrieve categories: Null reference error",
                    error = "NULL_REFERENCE_ERROR",
                    details = nullEx.Message,
                    stackTrace = nullEx.StackTrace,
                    source = nullEx.Source
                });
            }
            catch (Exception ex)
            {
                // ✅ MEJORA: Incluir más información de depuración
                return StatusCode(500, new { 
                    message = "Failed to retrieve categories",
                    error = "UNKNOWN_ERROR",
                    errorType = ex.GetType().Name,
                    details = ex.Message,
                    innerException = ex.InnerException?.Message,
                    stackTrace = ex.StackTrace,
                    source = ex.Source
                });
            }
        }

        [Authorize(Roles = "Admin")] // ✅ SOLO ADMIN: Solo administradores pueden crear categorías
        [HttpPost]
        public async Task<IActionResult> CreateCategory([FromBody] CreateCategoryDto createDto)
        {
            try
            {
                // ✅ VALIDACIÓN: Verificar que el nombre no esté vacío
                if (string.IsNullOrWhiteSpace(createDto.Name))
                {
                    return BadRequest(new { message = "El nombre de la categoría es requerido" });
                }

                // ✅ VALIDACIÓN: Verificar que no exista una categoría con el mismo nombre (case-insensitive)
                var existingCategory = await _context.Categories
                    .Where(c => c.Name.ToLower() == createDto.Name.ToLower().Trim())
                    .FirstOrDefaultAsync();

                if (existingCategory != null)
                {
                    return BadRequest(new { 
                        message = $"Ya existe una categoría con el nombre '{createDto.Name}'. Por favor, elige otro nombre.",
                        existingCategoryId = existingCategory.Id,
                        existingCategoryName = existingCategory.Name
                    });
                }

                // ✅ VALIDACIÓN: Si se proporciona ParentId, verificar que existe y que es una categoría padre (no una subcategoría)
                if (createDto.ParentId.HasValue)
                {
                    var parentCategory = await _context.Categories.FindAsync(createDto.ParentId.Value);
                    if (parentCategory == null)
                    {
                        return BadRequest(new { message = $"La categoría padre con ID {createDto.ParentId.Value} no existe" });
                    }
                    
                    // Verificar que la categoría padre no sea una subcategoría (no debe tener ParentId)
                    if (parentCategory.ParentId.HasValue)
                    {
                        return BadRequest(new { 
                            message = $"La categoría seleccionada (ID: {createDto.ParentId.Value}) es una subcategoría. Solo se pueden seleccionar categorías padre para crear subcategorías." 
                        });
                    }
                    
                    // Verificar que la categoría padre esté activa
                    if (!parentCategory.IsActive)
                    {
                        return BadRequest(new { 
                            message = $"La categoría padre seleccionada (ID: {createDto.ParentId.Value}) no está activa." 
                        });
                    }
                }

                var category = new Category
                {
                    Name = createDto.Name.Trim(), // Limpiar espacios en blanco
                    ParentId = createDto.ParentId,
                    IsActive = createDto.IsActive, // Usar el valor proporcionado (por defecto true en DTO)
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.Categories.Add(category);
                await _context.SaveChangesAsync();

                var isSubcategory = category.ParentId.HasValue;
                var categoryType = isSubcategory ? "subcategoría" : "categoría";
                var parentInfo = isSubcategory && category.ParentId.HasValue ? $" bajo la categoría padre (ID: {category.ParentId.Value})" : "";


                var categoryDto = _mapper.Map<CategoryDto>(category);
                return CreatedAtAction(nameof(GetCategories), new { id = category.Id }, new
                {
                    success = true,
                    message = $"{categoryType} '{category.Name}' creada exitosamente",
                    data = categoryDto
                });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx) when (dbEx.InnerException is Npgsql.PostgresException pgEx)
            {
                // ✅ Manejo específico de errores de PostgreSQL
                if (pgEx.SqlState == "23505") // Violación de restricción única
                {
                    if (pgEx.ConstraintName == "PK_Categories")
                    {
                        return StatusCode(500, new { 
                            message = "Error interno de base de datos: la secuencia de IDs está desincronizada. Por favor, contacta con el administrador del sistema.",
                            errorCode = "SEQUENCE_OUT_OF_SYNC",
                            constraintName = "PK_Categories"
                        });
                    }
                    
                    // Otra violación de restricción única (probablemente nombre duplicado que no se detectó antes)
                    return BadRequest(new { 
                        message = "Ya existe una categoría con este nombre o hay un conflicto en la base de datos.",
                        errorCode = "UNIQUE_CONSTRAINT_VIOLATION",
                        constraintName = pgEx.ConstraintName
                    });
                }
                return StatusCode(500, new { 
                    message = "Error de base de datos al crear la categoría",
                    errorCode = "DATABASE_ERROR",
                    sqlState = pgEx.SqlState
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to create category" });
            }
        }

        [Authorize(Roles = "Admin")] // ✅ SOLO ADMIN: Solo administradores pueden corregir secuencias
        [HttpPost("fix-sequence")]
        public async Task<IActionResult> FixCategoriesSequence()
        {
            try
            {
                // ✅ CORREGIR: Sincronizar la secuencia de IDs con el máximo ID existente
                var maxId = await _context.Categories.MaxAsync(c => (int?)c.Id) ?? 0;
                
                // Ejecutar SQL directo para corregir la secuencia
                var sql = $"SELECT setval('\"Categories_Id_seq\"', {maxId + 1}, false);";
                await _context.Database.ExecuteSqlRawAsync(sql);
                return Ok(new { 
                    message = "Secuencia corregida exitosamente",
                    maxId = maxId,
                    newSequenceValue = maxId + 1
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Error al corregir la secuencia", error = ex.Message });
            }
        }

        [Authorize]
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateCategory(int id, [FromBody] UpdateCategoryDto updateDto)
        {
            try
            {
                var category = await _context.Categories.FindAsync(id);
                if (category == null)
                {
                    return NotFound(new { message = "Category not found" });
                }

                category.Name = updateDto.Name;
                category.ParentId = updateDto.ParentId;
                category.IsActive = updateDto.IsActive;
                category.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var categoryDto = _mapper.Map<CategoryDto>(category);
                return Ok(categoryDto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Failed to update category" });
            }
        }
    }
}