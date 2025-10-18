using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.enums;
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
        private readonly IAuthorizationServices _authService;

        public AppointmentController(IAppointmentService appointmentService, ILogger<AppointmentController> logger, IAuthorizationServices authService)
        {
            _appointmentService = appointmentService;
            _logger = logger;
            _authService = authService;
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
        /// Subir reporte del experto (Experto)
        /// </summary>
        [HttpPost("submit-report/{appointmentId}")]
        public async Task<IActionResult> SubmitExpertReport(int appointmentId, [FromBody] SubmitExpertReportDto dto)
        {
            try
            {
                var userId = GetCurrentUserId();
                var appointment = await _appointmentService.SubmitExpertReportAsync(appointmentId, userId, dto.Notes);
                return Ok(appointment);
            }
            catch (UnauthorizedAccessException)
            {
                return Unauthorized(new { message = "Only the expert can submit reports" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting expert report for appointment: {AppointmentId}", appointmentId);
                return StatusCode(500, new { message = "Internal server error" });
            }
        }



        #region Admin Endpoints

        /// <summary>
        /// Obtener métricas de citas (Admin)
        /// </summary>
        [HttpGet("admin/metrics")]
        public async Task<IActionResult> GetAppointmentMetrics()
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
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
        /// Verificar timers de citas (Admin)
        /// </summary>
        [HttpPost("admin/check-timers")]
        public async Task<IActionResult> CheckAppointmentTimers()
        {
            try
            {
                // Verificar que el usuario sea admin
                if (!_authService.IsAdmin(User))
                {
                    return Forbid("Admin access required");
                }
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
