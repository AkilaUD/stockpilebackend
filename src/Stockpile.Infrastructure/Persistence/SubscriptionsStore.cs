using Microsoft.EntityFrameworkCore;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Subscriptions;
using Stockpile.Contracts.Errors;
using Stockpile.Domain.Entities.Catalogue;
using Stockpile.Domain.Entities.Subscriptions;
using Stockpile.Domain.Enums;

namespace Stockpile.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the subscription port. The integrity backstops live in
/// the database — one non-terminal subscription per supplier, unique plan codes and
/// invoice numbers, unique (provider, providerRef) payments, rowversions on invoices,
/// subscriptions and payments — and this store is where their failures become the
/// documented error codes.
/// </summary>
public sealed class SubscriptionsStore(StockpileDbContext db) : ISubscriptionsStore
{
    private const string OneOpenSubscriptionIndex = "UX_SupplierSubscriptions_Supplier_NonTerminal";
    private const string PlanCodeIndex = "IX_SubscriptionPlans_Code";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubscriptionPlan>> ListPlansAsync(bool activeOnly, CancellationToken cancellationToken)
    {
        IQueryable<SubscriptionPlan> query = db.SubscriptionPlans.AsNoTracking().Include(plan => plan.Entitlements);
        if (activeOnly)
        {
            query = query.Where(plan => plan.IsActive);
        }

        return await query
            .OrderBy(plan => plan.SortOrder)
            .ThenBy(plan => plan.Code)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SubscriptionPlan?> FindPlanByCodeAsync(string code, CancellationToken cancellationToken)
        => db.SubscriptionPlans
            .AsNoTracking()
            .Include(plan => plan.Entitlements)
            .FirstOrDefaultAsync(plan => plan.Code == code, cancellationToken);

    /// <inheritdoc />
    public Task<SubscriptionPlan?> FindPlanAsync(Guid planPublicId, bool forUpdate, CancellationToken cancellationToken)
    {
        IQueryable<SubscriptionPlan> query = db.SubscriptionPlans.Include(plan => plan.Entitlements);
        if (!forUpdate)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(plan => plan.PublicId == planPublicId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddPlanAsync(SubscriptionPlan plan, CancellationToken cancellationToken)
    {
        db.SubscriptionPlans.Add(plan);
        await SaveAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SubscriptionRecord?> FindOpenForSupplierAsync(
        Guid supplierId, bool forUpdate, CancellationToken cancellationToken)
        => ComposeAsync(
            db.SupplierSubscriptions.Where(subscription => subscription.SupplierId == supplierId
                && subscription.Status != SubscriptionStatus.Cancelled
                && subscription.Status != SubscriptionStatus.Expired),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public Task<SubscriptionRecord?> FindLatestForSupplierAsync(Guid supplierId, CancellationToken cancellationToken)
        => ComposeAsync(
            db.SupplierSubscriptions
                .Where(subscription => subscription.SupplierId == supplierId)
                .OrderByDescending(subscription => subscription.CreatedAt),
            forUpdate: false,
            cancellationToken);

    /// <inheritdoc />
    public Task<SubscriptionRecord?> FindForSupplierAsync(
        Guid supplierId, Guid subscriptionPublicId, bool forUpdate, CancellationToken cancellationToken)
        => ComposeAsync(
            // Ownership in the predicate: a foreign id and a missing id are indistinguishable.
            db.SupplierSubscriptions.Where(subscription => subscription.PublicId == subscriptionPublicId
                && subscription.SupplierId == supplierId),
            forUpdate,
            cancellationToken);

    /// <inheritdoc />
    public Task<bool> InvoiceNumberExistsAsync(string invoiceNumber, CancellationToken cancellationToken)
        => db.SubscriptionInvoices.AnyAsync(invoice => invoice.InvoiceNumber == invoiceNumber, cancellationToken);

    /// <inheritdoc />
    public async Task AddSubscriptionAsync(
        SupplierSubscription subscription, SubscriptionInvoice invoice, CancellationToken cancellationToken)
    {
        db.SupplierSubscriptions.Add(subscription);
        db.SubscriptionInvoices.Add(invoice);
        await SaveAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddPaymentAsync(SubscriptionPayment payment, CancellationToken cancellationToken)
    {
        db.SubscriptionPayments.Add(payment);
        await SaveAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InvoiceRecord?> FindInvoiceAsync(
        Guid invoicePublicId, bool forUpdate, CancellationToken cancellationToken)
    {
        IQueryable<SubscriptionInvoice> invoices = db.SubscriptionInvoices;
        IQueryable<SupplierSubscription> subscriptions = db.SupplierSubscriptions;
        if (!forUpdate)
        {
            invoices = invoices.AsNoTracking();
            subscriptions = subscriptions.AsNoTracking();
        }

        SubscriptionInvoice? invoice = await invoices
            .FirstOrDefaultAsync(candidate => candidate.PublicId == invoicePublicId, cancellationToken);
        return invoice is null ? null : await ComposeInvoiceAsync(invoice, subscriptions, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AdminInvoiceRecord>> ListInvoicesAsync(
        InvoiceStatus? status, int limit, CancellationToken cancellationToken)
    {
        IQueryable<SubscriptionInvoice> invoices = db.SubscriptionInvoices.AsNoTracking();
        if (status is { } filter)
        {
            invoices = invoices.Where(invoice => invoice.Status == filter);
        }

        var rows = await (
                from invoice in invoices
                join subscription in db.SupplierSubscriptions.AsNoTracking()
                    on invoice.SubscriptionId equals subscription.PublicId
                join supplier in db.Suppliers.AsNoTracking()
                    on subscription.SupplierId equals supplier.PublicId
                orderby invoice.IssuedAt descending
                select new { invoice, subscription, SupplierId = supplier.PublicId, supplier.DisplayName })
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new AdminInvoiceRecord(row.invoice, row.subscription, row.SupplierId, row.DisplayName))];
    }

    /// <inheritdoc />
    public Task<SubscriptionPayment?> FindPendingPaymentForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
        => db.SubscriptionPayments
            .AsNoTracking()
            .Where(payment => payment.InvoiceId == invoiceId && payment.Status == PaymentStatus.Pending)
            .OrderBy(payment => payment.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<PaymentRecord?> FindPaymentForUpdateAsync(
        string provider, string providerRef, CancellationToken cancellationToken)
    {
        SubscriptionPayment? payment = await db.SubscriptionPayments
            .FirstOrDefaultAsync(
                candidate => candidate.Provider == provider && candidate.ProviderRef == providerRef,
                cancellationToken);
        if (payment is null)
        {
            return null;
        }

        SubscriptionInvoice invoice = await db.SubscriptionInvoices
            .FirstAsync(candidate => candidate.PublicId == payment.InvoiceId, cancellationToken);
        InvoiceRecord record = await ComposeInvoiceAsync(invoice, db.SupplierSubscriptions, cancellationToken);
        return new PaymentRecord(payment, record);
    }

    /// <inheritdoc />
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AppException(ErrorCodes.ConcurrencyConflict, exception.Message, exception);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolationOn(exception, OneOpenSubscriptionIndex))
        {
            throw new AppException(ErrorCodes.SubscriptionExists);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolationOn(exception, PlanCodeIndex))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("code", "duplicate_code", "A plan with this code already exists.")]);
        }
        catch (DbUpdateException exception) when (SqlErrors.IsUniqueViolation(exception))
        {
            // A duplicate invoice number or (provider, providerRef) — a racing writer won.
            throw new AppException(ErrorCodes.ConcurrencyConflict, exception.Message, exception);
        }
    }

    private async Task<SubscriptionRecord?> ComposeAsync(
        IQueryable<SupplierSubscription> query, bool forUpdate, CancellationToken cancellationToken)
    {
        SupplierSubscription? subscription = await (forUpdate ? query : query.AsNoTracking())
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            return null;
        }

        SubscriptionPlan plan = await db.SubscriptionPlans
            .AsNoTracking()
            .Include(candidate => candidate.Entitlements)
            .FirstAsync(candidate => candidate.PublicId == subscription.PlanId, cancellationToken);

        IQueryable<SubscriptionInvoice> invoices = db.SubscriptionInvoices
            .Where(invoice => invoice.SubscriptionId == subscription.PublicId)
            .OrderByDescending(invoice => invoice.IssuedAt);
        List<SubscriptionInvoice> list = await (forUpdate ? invoices : invoices.AsNoTracking())
            .ToListAsync(cancellationToken);

        return new SubscriptionRecord(subscription, plan, list);
    }

    private async Task<InvoiceRecord> ComposeInvoiceAsync(
        SubscriptionInvoice invoice, IQueryable<SupplierSubscription> subscriptions, CancellationToken cancellationToken)
    {
        SupplierSubscription subscription = await subscriptions
            .FirstAsync(candidate => candidate.PublicId == invoice.SubscriptionId, cancellationToken);
        SubscriptionPlan plan = await db.SubscriptionPlans
            .AsNoTracking()
            .Include(candidate => candidate.Entitlements)
            .FirstAsync(candidate => candidate.PublicId == subscription.PlanId, cancellationToken);
        return new InvoiceRecord(invoice, subscription, plan);
    }
}
