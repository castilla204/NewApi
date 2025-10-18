namespace newApi.Common // Or newApi.DataLayer.Models, depending on your project structure
{
    public enum SearchHireStatus
    {
        Pending,
        AwaitingClientDecision,
        Disputed,
        Completed,
        Cancelled,                           // Cancelado (genérico)
        TransferFailed,
        DisputeResolvedClient,               // Disputa resuelta a favor del cliente
        DisputeResolvedExpert                // Disputa resuelta a favor del experto
    }

    public static class SearchHireStatusExtensions
    {
        public static string ToStringValue(this SearchHireStatus status)
        {
            return status switch
            {
                SearchHireStatus.Pending => "pending",
                SearchHireStatus.AwaitingClientDecision => "awaiting_client_decision",
                SearchHireStatus.Disputed => "disputed",
                SearchHireStatus.Completed => "completed",
                SearchHireStatus.Cancelled => "cancelled",
                SearchHireStatus.TransferFailed => "transfer_failed",
                SearchHireStatus.DisputeResolvedClient => "dispute-resolved-client",
                SearchHireStatus.DisputeResolvedExpert => "dispute-resolved-expert",
                _ => throw new ArgumentException($"Unknown status: {status}")
            };
        }

        public static string ToSpanishTranslation(this SearchHireStatus status)
        {
            return status switch
            {
                SearchHireStatus.Pending => "Pendiente",
                SearchHireStatus.AwaitingClientDecision => "Esperando decisión del cliente",
                SearchHireStatus.Disputed => "En disputa",
                SearchHireStatus.Completed => "Completado",
                SearchHireStatus.Cancelled => "Cancelado",
                SearchHireStatus.TransferFailed => "Transferencia fallida",
                SearchHireStatus.DisputeResolvedClient => "Disputa resuelta a favor del cliente",
                SearchHireStatus.DisputeResolvedExpert => "Disputa resuelta a favor del experto",
                _ => throw new ArgumentException($"Unknown status: {status}")
            };
        }

        public static SearchHireStatus FromStringValue(string value)
        {
            return value switch
            {
                "pending" => SearchHireStatus.Pending,
                "awaiting_client_decision" => SearchHireStatus.AwaitingClientDecision,
                "disputed" => SearchHireStatus.Disputed,
                "completed" => SearchHireStatus.Completed,
                "cancelled" => SearchHireStatus.Cancelled,
                "transfer_failed" => SearchHireStatus.TransferFailed,
                "dispute-resolved-client" => SearchHireStatus.DisputeResolvedClient,
                "dispute-resolved-expert" => SearchHireStatus.DisputeResolvedExpert,
                _ => throw new ArgumentException($"Invalid SearchHireStatus: {value}")
            };
        }

        public static string ToSpanishTranslation(this string statusString)
        {
            return statusString switch
            {
                "pending" => "Pendiente",
                "awaiting_client_decision" => "Esperando decisión del cliente",
                "disputed" => "En disputa",
                "completed" => "Completado",
                "cancelled" => "Cancelado",
                "transfer_failed" => "Transferencia fallida",
                "dispute-resolved-client" => "Disputa resuelta a favor del cliente",
                "dispute-resolved-expert" => "Disputa resuelta a favor del experto",
                _ => statusString // Si no se encuentra, devolver el original
            };
        }
    }
}