using MediatR;
using Microsoft.AspNetCore.Mvc;
using OtantikPos.Ordering.Application.Catalog;
using OtantikPos.Ordering.Application.Orders;

namespace OtantikPos.Node.Api.Controllers;

// Read-only: the menu belongs to the delivery system, which syncs it down. Changing it is done
// in the delivery system's admin.
[ApiController]
[Route("api/menu")]
public sealed class MenuController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<TillMenu> Get(CancellationToken cancellationToken) =>
        sender.Send(new GetMenuQuery(), cancellationToken);
}

// The mobile-number prompt for takeaway and delivery: who is this, and what are their points
// worth? Asks the delivery system, the record for customers; Status says Unavailable when it
// cannot be reached, rather than failing.
[ApiController]
[Route("api/customers")]
public sealed class CustomersController(ISender sender) : ControllerBase
{
    [HttpGet("by-phone/{phoneNumber}")]
    public Task<CustomerLookupResult> FindByPhone(string phoneNumber, CancellationToken cancellationToken) =>
        sender.Send(new LookupCustomerQuery(phoneNumber), cancellationToken);
}
