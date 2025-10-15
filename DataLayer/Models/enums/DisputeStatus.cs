namespace newApi.DataLayer.Models.enums
{
    /// <summary>
    /// Estados posibles de una disputa
    /// </summary>
    public enum DisputeStatus
    {
        Pending,
        Resolved
    }

    /// <summary>
    /// Extensiones para el enum DisputeStatus
    /// </summary>
    public static class DisputeStatusExtensions
    {
        /// <summary>
        /// Convierte el enum a su valor string
        /// </summary>
        public static string ToStringValue(this DisputeStatus status)
        {
            return status switch
            {
                DisputeStatus.Pending => "Pending",
                DisputeStatus.Resolved => "Resolved",
                _ => throw new ArgumentException($"Unknown status: {status}")
            };
        }

        /// <summary>
        /// Traduce el estado al español
        /// </summary>
        public static string ToSpanishTranslation(this DisputeStatus status)
        {
            return status switch
            {
                DisputeStatus.Pending => "Pendiente",
                DisputeStatus.Resolved => "Resuelta",
                _ => throw new ArgumentException($"Unknown status: {status}")
            };
        }

        /// <summary>
        /// Traduce un string de estado al español
        /// </summary>
        public static string ToSpanishTranslation(this string statusString)
        {
            return statusString switch
            {
                "Pending" => "Pendiente",
                "Resolved" => "Resuelta",
                _ => statusString // Si no se encuentra, devolver el original
            };
        }
    }
}














