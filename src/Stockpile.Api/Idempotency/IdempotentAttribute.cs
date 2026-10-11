namespace Stockpile.Api.Idempotency;

/// <summary>
/// Marks an action as a critical write that REQUIRES the
/// <c>Idempotency-Key</c> header (api.md §1.7, spec §14). Read by
/// <see cref="IdempotencyFilter"/> from endpoint metadata; applying it to a
/// controller covers every action on it.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class IdempotentAttribute : Attribute;
