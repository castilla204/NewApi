using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using newApi.Services;

namespace newApi.Controllers;

[Route("api/[controller]")]
[ApiController]
[EnableRateLimiting("support-chat")]
public class SupportChatController : ControllerBase
{
    private readonly ISupportChatService _supportChatService;
    private readonly ILogger<SupportChatController> _logger;

    public SupportChatController(ISupportChatService supportChatService, ILogger<SupportChatController> logger)
    {
        _supportChatService = supportChatService;
        _logger = logger;
    }

    /// <summary>Chat de soporte público (sin JWT).</summary>
    [HttpPost("message")]
    [AllowAnonymous]
    public async Task<IActionResult> SendMessage([FromBody] SupportChatRequest request, CancellationToken cancellationToken)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "El mensaje es obligatorio." });

        try
        {
            var reply = await _supportChatService.AskAsync(request.Message, request.History, cancellationToken);
            return Ok(new SupportChatResponse { Reply = reply, Success = true });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Support chat invalid argument");
            return BadRequest(new { message = "La solicitud no es válida.", errorCode = "SUPPORT_CHAT_INVALID_REQUEST" });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Support chat service unavailable");
            return StatusCode(503, new { message = "El servicio no está disponible temporalmente. Inténtalo de nuevo más tarde.", errorCode = "SUPPORT_CHAT_UNAVAILABLE" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Support chat error");
            return StatusCode(500, new { message = "Error al procesar el mensaje." });
        }
    }
}

public class SupportChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<SupportChatTurn>? History { get; set; }
}

public class SupportChatResponse
{
    public string Reply { get; set; } = string.Empty;
    public bool Success { get; set; }
}
