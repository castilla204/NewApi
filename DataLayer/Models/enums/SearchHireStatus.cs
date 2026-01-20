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
        DisputeResolvedExpert,               // Disputa resuelta a favor del experto
        CancelledByClientAccountDelete,      // Cancelado por eliminación de cuenta del cliente
        CancelledByExpertAccountDelete       // Cancelado por eliminación de cuenta del experto
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
                SearchHireStatus.DisputeResolvedClient => "dispute_resolved_client",
                SearchHireStatus.DisputeResolvedExpert => "dispute_resolved_expert",
                SearchHireStatus.CancelledByClientAccountDelete => "cancelled_by_client_account_delete",
                SearchHireStatus.CancelledByExpertAccountDelete => "cancelled_by_expert_account_delete",
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
                SearchHireStatus.CancelledByClientAccountDelete => "Cancelado - Cliente eliminó cuenta",
                SearchHireStatus.CancelledByExpertAccountDelete => "Cancelado - Experto eliminó cuenta",
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
                "dispute_resolved_client" => SearchHireStatus.DisputeResolvedClient,
                "dispute_resolved_expert" => SearchHireStatus.DisputeResolvedExpert,
                "cancelled_by_client_account_delete" => SearchHireStatus.CancelledByClientAccountDelete,
                "cancelled_by_expert_account_delete" => SearchHireStatus.CancelledByExpertAccountDelete,
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
                "dispute_resolved_client" => "Disputa resuelta a favor del cliente",
                "dispute_resolved_expert" => "Disputa resuelta a favor del experto",
                "cancelled_by_client_account_delete" => "Cancelado - Cliente eliminó cuenta",
                "cancelled_by_expert_account_delete" => "Cancelado - Experto eliminó cuenta",
                _ => statusString // Si no se encuentra, devolver el original
            };
        }
    }
}