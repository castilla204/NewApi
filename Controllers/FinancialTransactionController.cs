using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.DTOs;
using newApi.DataLayer.Models.PostGresModels;

namespace newApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FinancialTransactionController : ControllerBase
    {
        private readonly AppDbContext _context;

        public FinancialTransactionController(AppDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Obtener las transacciones financieras del usuario autenticado
        /// </summary>
        /// <param name="page">Número de página (por defecto: 1)</param>
        /// <param name="pageSize">Tamaño de página (por defecto: 20, máximo: 100)</param>
        /// <param name="transactionType">Filtrar por tipo de transacción (opcional): "Deposit", "ServicePayment", "Refund", "Payout"</param>
        /// <returns>Lista paginada de transacciones financieras</returns>
        [HttpGet("my-transactions")]
        public async Task<IActionResult> GetMyTransactions(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? transactionType = null)
        {
            try
            {
                // Obtener ID del usuario autenticado
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                {
                    return Unauthorized(new { message = "Invalid user identification" });
                }

                // Validar parámetros de paginación
                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 20;
                if (pageSize > 100) pageSize = 100; // Límite máximo

                // Construir query base
                var query = _context.FinancialTransactions
                    .Where(ft => ft.UserId == userId);

                // Filtrar por tipo de transacción si se proporciona
                if (!string.IsNullOrWhiteSpace(transactionType))
                {
                    query = query.Where(ft => ft.TransactionType == transactionType);
                }

                // Obtener total de registros antes de paginar
                var totalCount = await query.CountAsync();

                // Calcular paginación
                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var hasNextPage = page < totalPages;
                var hasPreviousPage = page > 1;

                // Obtener transacciones paginadas (ordenadas por fecha descendente - más recientes primero)
                var transactions = await query
                    .OrderByDescending(ft => ft.CreatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                // Mapear a DTOs
                var transactionDtos = transactions.Select(MapToDto).ToList();

                var response = new FinancialTransactionListResponseDto
                {
                    Transactions = transactionDtos,
                    TotalCount = totalCount,
                    Page = page,
                    PageSize = pageSize,
                    TotalPages = totalPages,
                    HasNextPage = hasNextPage,
                    HasPreviousPage = hasPreviousPage
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                // SEC: excepción no logueada (controlador sin ILogger)
                return StatusCode(500, new { message = "Error retrieving transactions", errorCode = "TRANSACTIONS_RETRIEVE_FAILED" });
            }
        }

        /// <summary>
        /// Mapear FinancialTransaction a FinancialTransactionDto
        /// </summary>
        private static FinancialTransactionDto MapToDto(FinancialTransaction transaction)
        {
            // Determinar si es ingreso o gasto
            var isPositive = transaction.TransactionType switch
            {
                "Deposit" => true,      // Ingreso
                "Payout" => true,        // Ingreso (para experto)
                "Refund" => true,        // Reembolso (ingreso)
                "ServicePayment" => false, // Gasto (pago de servicio)
                _ => transaction.Amount >= 0 // Por defecto, positivo si amount >= 0
            };

            // 🛡️ Round 27 — R27-T27-1-3 FIX:
            //   (1) Antes: $"{sign}€{transaction.Amount:F2}" producía '-€-100.00' para filas con
            //       Amount<0 (ServicePayment/TransferReversal/ChargebackReversal). Usar Math.Abs.
            //   (2) Antes: símbolo '€' hardcoded ignoraba transaction.Currency. Experto UK/USD/MXN
            //       veía '+€42.50' en lugar de '+£42.50'. Mapear símbolo por divisa.
            //   El snapshot Round 25 ya populaba transaction.Currency correctamente; faltaba reflejarlo.
            var sign = isPositive ? "+" : "-";
            var currencyCode = (transaction.Currency ?? "EUR").ToUpperInvariant();
            var currencySymbol = currencyCode switch
            {
                "EUR" => "€",
                "GBP" => "£",
                "USD" => "$",
                "MXN" => "MX$",
                "ARS" => "AR$",
                "BRL" => "R$",
                "CHF" => "CHF ",
                "JPY" => "¥",
                "CAD" => "CA$",
                "AUD" => "A$",
                _ => currencyCode + " "
            };
            var amountFormatted = $"{sign}{currencySymbol}{Math.Abs(transaction.Amount):F2}";

            // Nombre amigable del tipo de transacción
            var transactionTypeDisplay = transaction.TransactionType switch
            {
                "Deposit" => "Depósito",
                "ServicePayment" => "Pago de Servicio",
                "Refund" => "Reembolso",
                "Payout" => "Pago Recibido",
                _ => transaction.TransactionType
            };

            return new FinancialTransactionDto
            {
                Id = transaction.Id,
                Amount = transaction.Amount,
                Currency = transaction.Currency, // 🌍 Round 25: ISO 4217 snapshot heredado del SearchHire
                TransactionType = transaction.TransactionType,
                RelatedEntityType = transaction.RelatedEntityType,
                RelatedEntityId = transaction.RelatedEntityId,
                StripeTransferId = transaction.StripeTransferId,
                StripePaymentIntentId = transaction.StripePaymentIntentId,
                StripeRefundId = transaction.StripeRefundId,
                IsRefunded = transaction.IsRefunded,
                CreatedAt = transaction.CreatedAt,
                TransactionTypeDisplay = transactionTypeDisplay,
                AmountFormatted = amountFormatted,
                IsPositive = isPositive
            };
        }
    }
}

