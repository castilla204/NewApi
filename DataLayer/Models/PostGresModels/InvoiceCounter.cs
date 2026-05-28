using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace newApi.DataLayer.Models.PostGresModels
{
    /// <summary>
    /// Contador persistente de numeración correlativa de facturas, por serie y año.
    /// Clave compuesta (SeriesCode, Year). NextNumber = SIGUIENTE número a emitir.
    ///
    /// 🔧 FISCAL FLIP: solo se ESCRIBE cuando PlatformFiscal.IsVatRegistered=true.
    /// La numeración fiscal española debe ser CORRELATIVA SIN HUECOS (art. 6.1.a RD 1619/2012),
    /// por eso InvoiceNumberService serializa con SELECT ... FOR UPDATE en Postgres.
    /// </summary>
    [Table("InvoiceCounters")]
    public class InvoiceCounter
    {
        /// <summary>Código de serie (ej. "INSP-"). Forma parte de la PK compuesta.</summary>
        [Required]
        [MaxLength(32)]
        public string SeriesCode { get; set; } = string.Empty;

        /// <summary>Año fiscal de la serie. Forma parte de la PK. Permite reset anual sin colisiones.</summary>
        public int Year { get; set; }

        /// <summary>
        /// Siguiente número a emitir (1-based). Tras reservarlo, se incrementa y persiste en la MISMA
        /// transacción dentro de InvoiceNumberService.NextAsync, garantizando no-hueco entre réplicas.
        /// </summary>
        public long NextNumber { get; set; } = 1;

        /// <summary>Auditoría: momento de creación de la fila.</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Auditoría: último update (= último número reservado para esa serie/año).</summary>
        public DateTime? UpdatedAt { get; set; }
    }
}
