namespace newApi.Configuration
{
    /// <summary>
    /// Perfil fiscal de la plataforma. Cargado desde la sección "PlatformFiscal" de appsettings
    /// (o env vars de Render: PlatformFiscal__*).
    ///
    /// CONTRATO DE ACTIVACIÓN ("flip"):
    ///   IsVatRegistered=false (DEFAULT, pre-alta) → comportamiento legacy:
    ///     - Recibo simple (sin NIF emisor, sin numeración correlativa, sin menciones legales).
    ///     - D11 no alerta cuando Stripe devuelve IVA=0 (es la situación normal, no incidencia).
    ///     - NIF cliente se persiste igualmente desde Stripe (preparado para el día del flip).
    ///   IsVatRegistered=true (post-alta como autónomo/empresa) → factura fiscal completa:
    ///     - Numeración correlativa real (vía InvoiceCounters + IInvoiceNumberService).
    ///     - PDF con NIF emisor, razón social, dirección fiscal e IAE.
    ///     - Mención de inversión del sujeto pasivo cuando IVA=0 y cliente intracomunitario.
    ///     - D11 vuelve a alertar (Critical + RequiresManualReview) ante IVA=0 sin reverse-charge.
    ///
    /// Flip operativo (sin recompilar):
    ///   1) Aplicar migración: dotnet ef database update --project NewApi
    ///   2) Rellenar env vars en Render: PlatformFiscal__* (ver README de despliegue).
    ///   3) Poner PlatformFiscal__IsVatRegistered=true.
    ///   4) Reiniciar el web service.
    /// </summary>
    public class PlatformFiscalProfile
    {
        public const string SectionName = "PlatformFiscal";

        /// <summary>
        /// Master switch. false (default) = la plataforma NO está dada de alta como autónomo/empresa.
        /// true = la plataforma está registrada fiscalmente y emite factura formal.
        /// </summary>
        public bool IsVatRegistered { get; set; } = false;

        /// <summary>Razón social / nombre legal del emisor (ej. "Diego Castilla Blanco").</summary>
        public string LegalName { get; set; } = string.Empty;

        /// <summary>NIF/CIF del emisor (ej. "12345678Z" para autónomo persona física).</summary>
        public string Nif { get; set; } = string.Empty;

        /// <summary>Dirección fiscal del emisor.</summary>
        public FiscalAddressOptions FiscalAddress { get; set; } = new FiscalAddressOptions();

        /// <summary>Epígrafe IAE (ej. "843.9" para servicios técnicos / "769.9" para servicios online).</summary>
        public string IaeCode { get; set; } = string.Empty;

        /// <summary>Alta en Registro de Operadores Intracomunitarios (ROI/VIES). Necesario para reverse-charge B2B intra-UE.</summary>
        public bool RoiRegistered { get; set; } = false;

        /// <summary>Alta en One-Stop Shop (Union OSS) para B2C UE tras cruzar el umbral €10.000/año.</summary>
        public bool OssRegistered { get; set; } = false;

        /// <summary>Prefijo de la serie de facturación (ej. "INSP-"). Se concatena con año y nº.</summary>
        public string InvoiceSeriesPrefix { get; set; } = "INSP-";

        /// <summary>
        /// Año de la serie. Si es 0 (default), se usa el año UTC actual al numerar (reset anual estándar).
        /// Forzar otro valor solo si se quiere continuidad cross-year.
        /// </summary>
        public int InvoiceSeriesYear { get; set; } = 0;

        /// <summary>
        /// True solo si el perfil tiene datos completos para emitir factura formal.
        /// Los servicios consumidores deben llamar a este método (fail-fast) en vez de mirar IsVatRegistered
        /// directamente, para evitar facturas "a medias" si el flip se hizo sin rellenar todos los campos.
        /// </summary>
        public bool IsReadyForFlip()
        {
            if (!IsVatRegistered) return false;
            if (string.IsNullOrWhiteSpace(LegalName)) return false;
            if (string.IsNullOrWhiteSpace(Nif)) return false;
            if (FiscalAddress == null) return false;
            if (string.IsNullOrWhiteSpace(FiscalAddress.Street)) return false;
            if (string.IsNullOrWhiteSpace(FiscalAddress.PostalCode)) return false;
            if (string.IsNullOrWhiteSpace(FiscalAddress.City)) return false;
            if (string.IsNullOrWhiteSpace(FiscalAddress.Country)) return false;
            if (string.IsNullOrWhiteSpace(InvoiceSeriesPrefix)) return false;
            return true;
        }
    }

    public class FiscalAddressOptions
    {
        public string Street { get; set; } = string.Empty;
        public string PostalCode { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Province { get; set; } = string.Empty;
        /// <summary>ISO 3166-1 alpha-2 (ej. "ES").</summary>
        public string Country { get; set; } = string.Empty;
    }
}
