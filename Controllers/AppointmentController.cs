using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.Services;
using System.Security.Claims;

namespace newApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class AppointmentController : ControllerBase
    {
        private readonly IAppointmentService _appointmentService;
        private readonly ILogger<AppointmentController> _logger;

        public AppointmentController(IAppointmentService appointmentService, ILogger<AppointmentController> logger)
        {
            _appointmentService = appointmentService;
            _logger = logger;
        }

        /// <summary>
        /// Obtener una cita por ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetAppointment(int id)
        {
            try
            {
                var appointment = await _appointmentService.GetAppointmentAsync(id);
                if (appointment == null)
                    return NotFound(new { message = "Appointment not found" });

                return Ok(appointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment: {AppointmentId}", id);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener una cita por SearchHire ID
        /// </summary>
        [HttpGet("search-hire/{searchHireId}")]
        public async Task<IActionResult> GetAppointmentBySearchHireId(int searchHireId)
        {
            try
            {
                var appointment = await _appointmentService.GetAppointmentBySearchHireIdAsync(searchHireId);
                if (appointment == null)
                    return NotFound(new { message = "Appointment not found" });

                return Ok(appointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment by SearchHire ID: {SearchHireId}", searchHireId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener todas las citas del usuario
        /// </summary>
        [HttpGet("my-appointments")]
        public async Task<IActionResult> GetMyAppointments()
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointments = await _appointmentService.GetUserAppointmentsAsync(userId);
                return Ok(appointments);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user appointments for user: {UserId}", GetCurrentUserId());
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Crear una nueva cita
        /// </summary>
        [HttpPost("create")]
        public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentDto dto)
        {
            try
            {
                var appointment = await _appointmentService.CreateAppointmentAsync(dto);
                return CreatedAtAction(nameof(GetAppointment), new { id = appointment.Id }, appointment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating appointment for SearchHire: {SearchHireId}", dto.SearchHireId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Proponer una cita (Cliente)
        /// </summary>
        [HttpPost("propose/{searchHireId}")]
        public async Task<IActionResult> ProposeAppointment(int searchHireId, [FromBody] ProposeAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.ProposeAppointmentAsync(searchHireId, dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client can propose appointments" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error proposing appointment for SearchHire: {SearchHireId}", searchHireId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Confirmar una cita (Experto)
        /// </summary>
        [HttpPost("confirm")]
        public async Task<IActionResult> ConfirmAppointment([FromBody] ConfirmAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.ConfirmAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can confirm appointments" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error confirming appointment: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Rechazar una cita (Experto)
        /// </summary>
        [HttpPost("reject")]
        public async Task<IActionResult> RejectAppointment([FromBody] RejectAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.RejectAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can reject appointments" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rejecting appointment: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Cancelar una cita (Cliente o Experto)
        /// </summary>
        [HttpPost("cancel")]
        public async Task<IActionResult> CancelAppointment([FromBody] CancelAppointmentDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.CancelAppointmentAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client or expert can cancel appointments" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling appointment: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Marcar una cita como completada (Cliente o Experto)
        /// </summary>
        [HttpPost("mark-completed")]
        public async Task<IActionResult> MarkCompleted([FromBody] MarkCompletedDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.MarkCompletedAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client or expert can mark appointments as completed" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking appointment as completed: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Crear una disputa de cita (SOLO CLIENTE)
        /// </summary>
        [HttpPost("create-dispute")]
        public async Task<IActionResult> CreateDispute([FromBody] CreateAppointmentDisputeDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.CreateDisputeAsync(dto, userId);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the client can create disputes" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating appointment dispute: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #region Admin Endpoints

        /// <summary>
        /// Obtener métricas de citas (Admin)
        /// </summary>
        [HttpGet("admin/metrics")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAppointmentMetrics()
        {
            try
            {
                var metrics = await _appointmentService.GetAppointmentMetricsAsync();
                return Ok(metrics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment metrics");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Obtener todas las disputas de citas (Admin)
        /// </summary>
        [HttpGet("admin/disputes")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetAppointmentDisputes()
        {
            try
            {
                var disputes = await _appointmentService.GetAppointmentDisputesAsync();
                return Ok(disputes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting appointment disputes");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Resolver una disputa de cita (Admin)
        /// </summary>
        [HttpPost("admin/resolve-dispute")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResolveDispute([FromBody] ResolveAppointmentDisputeDto dto)
        {
            try
            {
                var adminId = GetCurrentUserId();
                var success = await _appointmentService.ResolveDisputeAsync(dto, adminId);
                
                if (success)
                    return Ok(new { message = "Dispute resolved successfully" });
                else
                    return BadRequest(new { message = "Failed to resolve dispute" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving appointment dispute: {AppointmentId}", dto.AppointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        /// <summary>
        /// Verificar timers de citas (Admin)
        /// </summary>
        [HttpPost("admin/check-timers")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CheckAppointmentTimers()
        {
            try
            {
                await _appointmentService.CheckAppointmentTimersAsync();
                return Ok(new { message = "Appointment timers checked successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking appointment timers");
                return StatusCode(500, new { message = "Internal server error" });
            }
        }

        #endregion

        #region Private Methods

        private int GetCurrentUserId()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
            {
                throw new UnauthorizedAccessException("Invalid user ID");
            }
            return userId;
        }

        #endregion
    }
}
