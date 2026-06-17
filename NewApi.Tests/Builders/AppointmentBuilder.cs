using Microsoft.EntityFrameworkCore;
using newApi.DataLayer.Models;
using newApi.DataLayer.Models.PostGresModels;

namespace NewApi.Tests.Builders;

/// <summary>
/// Crea un Appointment vinculado a un SearchHire. Estado por defecto:
/// awaiting_appointment (resuelto contra el seed por StatusValue).
/// </summary>
public class AppointmentBuilder
{
    private readonly int _searchHireId;
    private string _statusValue = "awaiting_appointment";
    private DateTime? _proposedDate;
    private string? _proposedTime;
    private string? _location;
    private int _rejectionCount;
    private int _clientCancellationCount;
    private int _expertCancellationCount;
    private int? _expertId;
    private DateTime? _startsAtUtc;
    private DateTime? _endsAtUtc;
    private bool _blocksCalendar;

    public AppointmentBuilder(int searchHireId) { _searchHireId = searchHireId; }

    public AppointmentBuilder WithStatusValue(string statusValue) { _statusValue = statusValue; return this; }
    public AppointmentBuilder WithProposal(DateTime date, string time, string location = "Test location")
    {
        _proposedDate = date;
        _proposedTime = time;
        _location = location;
        return this;
    }
    public AppointmentBuilder WithRejectionCount(int n) { _rejectionCount = n; return this; }
    public AppointmentBuilder WithClientCancellations(int n) { _clientCancellationCount = n; return this; }
    public AppointmentBuilder WithExpertCancellations(int n) { _expertCancellationCount = n; return this; }
    public AppointmentBuilder WithSlot(int expertId, DateTime startsAtUtc, DateTime endsAtUtc, bool blocksCalendar = true)
    {
        _expertId = expertId;
        _startsAtUtc = startsAtUtc;
        _endsAtUtc = endsAtUtc;
        _blocksCalendar = blocksCalendar;
        return this;
    }

    public async Task<Appointment> PersistAsync(AppDbContext db)
    {
        var status = await db.SystemStatuses
            .FirstOrDefaultAsync(s => s.StatusType == "AppointmentStatus" && s.StatusValue == _statusValue);

        if (status is null)
            throw new InvalidOperationException(
                $"AppointmentStatus '{_statusValue}' no encontrado. ¿Seed cargado?");

        var appt = new Appointment
        {
            SearchHireId = _searchHireId,
            StatusId = status.Id,
            ProposedDate = _proposedDate,
            Location = _location,
            RejectionCount = _rejectionCount,
            ClientCancellationCount = _clientCancellationCount,
            ExpertCancellationCount = _expertCancellationCount,
            ExpertId = _expertId,
            StartsAtUtc = _startsAtUtc,
            EndsAtUtc = _endsAtUtc,
            BlocksCalendar = _blocksCalendar,
        };

        db.Appointments.Add(appt);
        await db.SaveChangesAsync();
        return appt;
    }
}
