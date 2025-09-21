namespace newApi.Common // Or newApi.DataLayer.Models, depending on your project structure
{
    public enum SearchHireStatus
    {
        Pending,
        AwaitingClientDecision,
        Disputed,
        Completed,
        Cancelled,
        TransferFailed,
        DisputeResolved
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
                SearchHireStatus.DisputeResolved => "dispute-resolved",
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
                SearchHireStatus.DisputeResolved => "Disputa resuelta",
                _ => throw new ArgumentException($"Unknown status: {status}")
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
                "dispute-resolved" => "Disputa resuelta",
                _ => statusString // Si no se encuentra, devolver el original
            };
        }
    }
}