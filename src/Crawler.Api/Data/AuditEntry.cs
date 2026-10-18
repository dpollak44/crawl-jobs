namespace Crawler.Api.Data;

/// <summary>
/// Who changed what, when, from what to what. Written in the same transaction as the change, so there is
/// never a change without its audit row. Healthcare billing is audited; this is the table the auditor reads.
/// Actor comes from the authenticated user once auth exists; until then it is the caller's X-Actor header or "anonymous".
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public string Entity { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? From { get; set; }
    public string? To { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }

    public static AuditEntry For(string entity, Guid entityId, string action, string? from, string? to, string actor, DateTimeOffset at) => new()
    {
        Entity = entity,
        EntityId = entityId,
        Action = action,
        From = from,
        To = to,
        Actor = actor,
        At = at,
    };
}
