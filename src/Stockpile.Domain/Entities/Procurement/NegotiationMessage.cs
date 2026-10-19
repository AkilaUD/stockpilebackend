using Stockpile.Domain.Enums;

namespace Stockpile.Domain.Entities.Procurement;

/// <summary>
/// One typed message in a quote's negotiation thread (domain-model §6.3). A
/// buyer's revision request is recorded here as a <see cref="NegotiationKind"/>
/// plus a bounded note, so the supplier answers a specific ask instead of
/// guessing from free text. Append-only: a message is never edited or deleted.
/// <para>Public id is Guid <b>v4</b> (sensitive), like the quote it belongs to.</para>
/// </summary>
public sealed class NegotiationMessage
{
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public Guid QuoteId { get; set; }

    /// <summary>The revision the message was written against (what the buyer was looking at).</summary>
    public int AgainstRevisionNumber { get; set; }

    public NegotiationDirection Direction { get; set; }

    public NegotiationKind Kind { get; set; }

    /// <summary>Bounded free-text note, nvarchar(1000). Never logged (Z4).</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Acting user's public GUID.</summary>
    public Guid CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
