namespace BetStats.Application.Governance;

// Selected by trusted host code, never bound from HTTP input. Boolean callers
// retain the legacy public-display (true) / internal-processing (false) contract.
public readonly record struct InspectionAudience
{
    private InspectionAudience(int kind) => Kind = kind;
    private int Kind { get; }
    public static InspectionAudience PrivateOperatorInspection => new(2);
    public bool IsPrivate => Kind == 2;
    public bool IsPublic => Kind == 1;
    public BetStats.Domain.Governance.DataPurpose Purpose => IsPublic
        ? BetStats.Domain.Governance.DataPurpose.PublicDisplay : BetStats.Domain.Governance.DataPurpose.InternalAnalytics;
    public UsageContext Context => new(PublicDisplay: IsPublic, IntendedRetentionDays: 1);
    public static implicit operator InspectionAudience(bool display) => new(display ? 1 : 0);
    public static implicit operator bool(InspectionAudience audience) => audience.Kind != 0;
}

// Request-scoped capability. The endpoint sets it only after Host/Origin/remote
// checks and clears it in finally, including failed or cancelled reads.
public sealed class OperatorInspectionContext
{
    public bool Authorized { get; private set; }
    public InspectionAudience Audience => Authorized ? InspectionAudience.PrivateOperatorInspection : (InspectionAudience)true;
    public void Begin() => Authorized = true;
    public void End() => Authorized = false;
}
