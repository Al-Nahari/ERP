namespace ERP.Persistence;

/// <summary>
/// Backing table for atomic, gap-free-per-key document numbering (journal
/// entries, invoices, purchase orders, ...). A plain persistence-layer utility
/// entity, not a domain aggregate — numbering is an infrastructure concern, not
/// a business rule, so it deliberately lives outside ERP.Domain.
/// </summary>
public class NumberSequence
{
    public string SequenceKey { get; set; } = default!; // e.g. "JE-{companyId}-{year}"
    public long CurrentValue { get; set; }
}
