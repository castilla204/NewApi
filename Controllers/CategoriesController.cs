using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AutoMapper;
using newApi.DataLayer.Models.PostGresModels;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models;
using Npgsql;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CategoriesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IMapper _mapper;

        public CategoriesController(AppDbContext context, IMapper mapper)
        {
            _context = context;
            _mapper = mapper;
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
        public async Task<IActionResult> GetCategories()
        {
            try
            {
                // ✅ CORRECCIÓN: Verificar conexión antes de consultar
                if (!await _context.Database.CanConnectAsync())
                {
                    return StatusCode(503, new { 
                        message = "Database connection unavailable. Please check your SSH tunnel.",
                        error = "DATABASE_CONNECTION_LOST"
                    });
                }

                var categories = await _context.Categories
                    .AsNoTracking() // ✅ MEJORA: No tracking para mejor rendimiento y evitar problemas de conexión
                    .Where(c => c.IsActive)
                    .Include(c => c.Subcategories)
                    .ToListAsync();

                var categoryDtos = categories.Select(c => new CategoryWithDetailsDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    ParentId = c.ParentId,
                    IsActive = c.IsActive,
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    IsParent = c.ParentId == null,
                    HasSubcategories = c.Subcategories != null && c.Subcategories.Any(sc => sc.IsActive),
                    SubcategoriesCount = c.Subcategories != null ? c.Subcategories.Count(sc => sc.IsActive) : 0
                }).ToList();

                return Ok(categoryDtos);
            }
            catch (Npgsql.NpgsqlException npgsqlEx)
            {
                // ✅ CORRECCIÓN: Manejo específico de errores de PostgreSQL
                // SqlState puede no estar disponible en NpgsqlException, intentar obtenerlo del PostgresException interno
                var sqlState = (npgsqlEx.InnerException as Npgsql.PostgresException)?.SqlState ?? "UNKNOWN";
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
            catch (Exception ex)
            {
                return StatusCode(500, new { 
                    message = "Failed to retrieve categories",
                    error = "UNKNOWN_ERROR",
                    details = ex.Message
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