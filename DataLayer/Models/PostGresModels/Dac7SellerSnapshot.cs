using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// 🛡️ Round 28 MUD-BB: snapshot anual por seller para reporting DAC7 modelo 238.
    ///
    /// Una fila por (ExpertProfileId, Year). Se rellena al cierre del año natural por
    /// Hangfire RecurringJob @20-enero 02:00 UTC. El XML modelo 238 se genera filtrando
    /// solo filas que cruzaron umbral (>= €2.000 anuales OR >= 30 tx) — ver
    /// Dac7AnnualReportService.GenerateForYear.
    ///
    /// Los campos legales (LegalName/DOB/TIN/IBAN/Address) se snapshotean del estado
    /// Stripe del experto en el momento del snapshot — NO se re-leen al exportar el XML
    /// porque (a) puede haber cambiado entretanto, (b) tenemos que reportar el estado
    /// fiscal al cierre del año, (c) si el experto borró cuenta GDPR los datos legales
    /// originales se preservan aquí (excepción legal Art.17 — obligación legal).
    /// </summary>
    [Table("Dac7SellerSnapshots")]
    public class Dac7SellerSnapshot
    {
        public int Id { get; set; }

        [Required]
        public int ExpertProfileId { get; set; }

        [Required]
        public int Year { get; set; }

        // ----- Datos legales (snapshot del estado Stripe Connect al cierre del año) -----
        [MaxLength(200)]
        public string? LegalFirstName { get; set; }

        [MaxLength(200)]
        public string? LegalLastName { get; set; }

        public DateOnly? DateOfBirth { get; set; }

        [MaxLength(2)]
        public string? TinCountry { get; set; } // ISO-2 país emisor del TIN

        [MaxLength(50)]
        public string? TinValue { get; set; } // NIF/DNI/SSN

        [MaxLength(2)]
        public string? FiscalCountry { get; set; } // ISO-2 país residencia fiscal

        [MaxLength(50)]
        public string? VatNumber { get; set; } // NIF IVA UE si B2B intracomunitario

        [MaxLength(300)]
        public string? AddressLine { get; set; }

        [MaxLength(100)]
        public string? AddressCity { get; set; }

        [MaxLength(20)]
        public string? AddressPostalCode { get; set; }

        [MaxLength(8)]
        public string? IbanLast4 { get; set; } // solo los últimos 4 dígitos (no PII completo)

        // ----- Agregados anuales (por trimestre + total) -----
        public decimal GrossSalesQ1 { get; set; }
        public decimal GrossSalesQ2 { get; set; }
        public decimal GrossSalesQ3 { get; set; }
        public decimal GrossSalesQ4 { get; set; }

        public decimal PlatformFeesQ1 { get; set; }
        public decimal PlatformFeesQ2 { get; set; }
        public decimal PlatformFeesQ3 { get; set; }
        public decimal PlatformFeesQ4 { get; set; }

        public int TransactionCountQ1 { get; set; }
        public int TransactionCountQ2 { get; set; }
        public int TransactionCountQ3 { get; set; }
        public int TransactionCountQ4 { get; set; }

        // Moneda en la que están expresados los agregados arriba. Si el experto tuvo
        // hires en varias monedas, el reporting español requiere convertir a EUR.
        // Por simplicidad almacenamos EUR; conversiones FX van en GrossSales* directamente
        // usando ExchangeRateService a fecha del hire.
        [MaxLength(3)]
        public string ReportingCurrency { get; set; } = "EUR";

        // ----- Metadata -----
        public DateTime SnapshotCreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReportedToAeatAt { get; set; } // Marca cuando admin sube el XML a AEAT

        [MaxLength(50)]
        public string? AeatSubmissionReference { get; set; } // Justificante AEAT

        // ----- Navegación -----
        [ForeignKey("ExpertProfileId")]
        public virtual ExpertProfile? ExpertProfile { get; set; }

        // Computed properties (no persistidas)
        [NotMapped]
        public decimal TotalGrossSales => GrossSalesQ1 + GrossSalesQ2 + GrossSalesQ3 + GrossSalesQ4;

        [NotMapped]
        public decimal TotalPlatformFees => PlatformFeesQ1 + PlatformFeesQ2 + PlatformFeesQ3 + PlatformFeesQ4;

        [NotMapped]
        public int TotalTransactionCount => TransactionCountQ1 + TransactionCountQ2 + TransactionCountQ3 + TransactionCountQ4;

        /// <summary>True si el seller cruzó el umbral DAC7 (€2.000 OR 30 tx).</summary>
        [NotMapped]
        public bool CrossedReportingThreshold => TotalGrossSales >= 2000m || TotalTransactionCount >= 30;
    }
}
