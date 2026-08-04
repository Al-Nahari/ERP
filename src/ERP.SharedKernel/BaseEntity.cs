using System;
using System.Collections.Generic;

namespace ERP.SharedKernel;

/// <summary>
/// Base class for every entity in the system. Provides identity, full audit trail
/// (Created/Updated by + timestamp), and soft-delete support so that no financial
/// or operational record is ever physically removed from the database — a hard
/// requirement for auditability in an ERP system.
/// </summary>
public abstract class BaseEntity : IAuditable, ISoftDeletable
{
    /// <summary>Surrogate primary key. GUID is used instead of identity int so that
    /// entities can be created offline / in distributed scenarios (e.g. mobile POS,
    /// multi-database sharding by company) without collision.</summary>
    public Guid Id { get; protected set; } = Guid.NewGuid();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? UpdatedDate { get; set; }
    public string? UpdatedBy { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedDate { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>Row version for optimistic concurrency — critical in an ERP where
    /// multiple users may post to the same document (e.g. two clerks approving the
    /// same Purchase Order) simultaneously.</summary>
    public byte[]? RowVersion { get; set; }

    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>Domain events raised by this entity, dispatched after successful
    /// persistence (see MediatR DomainEventDispatcher in Infrastructure).</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    protected void RemoveDomainEvent(IDomainEvent domainEvent) => _domainEvents.Remove(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Marker + contract for auditable entities.</summary>
public interface IAuditable
{
    DateTime CreatedDate { get; set; }
    string CreatedBy { get; set; }
    DateTime? UpdatedDate { get; set; }
    string? UpdatedBy { get; set; }
}

/// <summary>Contract for soft-deletable entities. Global query filters in
/// ERP.Persistence automatically exclude IsDeleted == true rows.</summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }
    DateTime? DeletedDate { get; set; }
    string? DeletedBy { get; set; }
}

/// <summary>Marker interface for domain events raised inside aggregates.</summary>
public interface IDomainEvent
{
    DateTime OccurredOn { get; }
}
