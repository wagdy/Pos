using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Customers;
using Otantik.SharedKernel.Orders;
using OtantikPos.Ordering.Application.Ports;
using OtantikPos.Ordering.Domain;

namespace OtantikPos.Ordering.Application.Orders;

public enum CustomerLookupStatus
{
    Found,

    // No account has this number: the customer is a walk-in.
    NotFound,

    // The delivery system could not be reached. The number is still usable on the order; the
    // profile and points are not.
    Unavailable,
}

// RedemptionValuePer100Points is the rate in force, so the till can show what any number of
// points is worth before applying them: LoyaltyRedemption.DiscountFor(points, rate).
public sealed record CustomerLookupResult(CustomerLookupStatus Status, CustomerProfile? Profile, decimal RedemptionValuePer100Points)
{
    // What the balance is worth off a bill right now, so the cashier can tell the customer.
    public decimal PointsValue => Profile is null ? 0 : LoyaltyRedemption.DiscountFor(Profile.PointsBalance, RedemptionValuePer100Points);
}

internal static class CustomerLookup
{
    public static async Task<(CustomerLookupStatus Status, CustomerProfile? Profile)> FindAsync(
        ICustomerDirectory directory, string phoneNumber, CancellationToken cancellationToken)
    {
        try
        {
            var profile = await directory.FindByPhoneAsync(phoneNumber.Trim(), cancellationToken);
            return (profile is null ? CustomerLookupStatus.NotFound : CustomerLookupStatus.Found, profile);
        }
        catch (DeliverySystemUnavailableException)
        {
            return (CustomerLookupStatus.Unavailable, null);
        }
    }
}

// The till's mobile-number prompt for takeaway and delivery: who is this, and what are their
// points worth?
public sealed record LookupCustomerQuery(string PhoneNumber) : IRequest<CustomerLookupResult>;

internal sealed class LookupCustomerHandler(ICurrentUser user, ICustomerDirectory customers, IPricingSettings pricing)
    : IRequestHandler<LookupCustomerQuery, CustomerLookupResult>
{
    public async Task<CustomerLookupResult> Handle(LookupCustomerQuery request, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.OrderCreate))
            throw new ForbiddenException("You are not allowed to look customers up.");

        var (status, profile) = await CustomerLookup.FindAsync(customers, request.PhoneNumber, cancellationToken);
        return new CustomerLookupResult(status, profile, await pricing.GetRedemptionValuePer100PointsAsync(cancellationToken));
    }
}

public sealed record AttachCustomerCommand(Guid OrderId, string PhoneNumber, string? Name = null) : IRequest<AttachCustomerResult>;

// Lookup says whether the number found an account, so the till can tell "a walk-in" from "the
// delivery system is unreachable, try again".
public sealed record AttachCustomerResult(Order Order, CustomerLookupStatus Lookup);

internal sealed class AttachCustomerHandler(OrderWorkflow workflow, ICustomerDirectory customers)
    : IRequestHandler<AttachCustomerCommand, AttachCustomerResult>
{
    public async Task<AttachCustomerResult> Handle(AttachCustomerCommand request, CancellationToken cancellationToken)
    {
        // Attaching a customer is editing an open order, the same right as adding to it.
        var order = await workflow.LoadForAsync(OrderAction.AddItems, request.OrderId, cancellationToken);
        var lookup = await CustomerLookup.FindAsync(customers, request.PhoneNumber, cancellationToken);
        var tax = await workflow.TaxPercentageAsync(cancellationToken);

        TillOperations.AttachCustomer(order, lookup.Profile, request.PhoneNumber, request.Name, tax, workflow.Now);

        return new AttachCustomerResult(await workflow.CommitAsync(order, cancellationToken), lookup.Status);
    }
}
