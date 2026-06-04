using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BD: cola de pérdidas pendientes de recuperar off-Stripe.
    ///
    /// Se insertan filas cuando:
    /// 1. MUD-AK: relocation/deletion cierra cuenta con Stripe Pending > 0 → dinero
    ///    revierte al platform. Admin debe enviarlo manualmente al experto cuando
    ///    Stripe libere settlement (2-7d).
    /// 2. MUD-AV: clawback imposible porque experto ya retiró balance Stripe Connect.
    ///    Admin debe reclamar off-Stripe (transferencia directa expert→platform).
    ///
    /// Antes solo había logs Critical → si admin no estaba al día con Slack, el
    /// dinero quedaba en limbo indefinidamente. Esta cola permite un dashboard
    /// admin (GET /api/admin/clawback-queue) y una métrica "platform debt to users".
    /// </summary>
    [Table("ClawbackQueues")]
    public class ClawbackQueue
    {
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [MaxLength(50)]
        public string? StripeAccountId { get; set; }

        public int? SearchHireId { get; set; } // null si la pérdida no es por un hire específico

        [Required]
        public decimal AmountMajor { get; set; }

        [Required]
        [MaxLength(3)]
        public string Currency { get; set; } = "EUR";

        /// <summary>"PendingBalance" (MUD-AK) | "WithdrawnBalance" (MUD-AV) | "InFlightTransfer" (MUD-BE)</summary>
        [Required]
        [MaxLength(40)]
        public string Reason { get; set; } = string.Empty;

        [MaxLength(2000)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Marca cuando admin resolvió el caso (transferencia hecha o pérdida asumida).</summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>"PaidToUser" | "AbsorbedAsLoss" | "InvalidEntry"</summary>
        [MaxLength(40)]
        public string? Resolution { get; set; }

        [MaxLength(2000)]
        public string? ResolutionNotes { get; set; }

        [MaxLength(200)]
        public string? ResolvedByAdminEmail { get; set; }
    }
}
