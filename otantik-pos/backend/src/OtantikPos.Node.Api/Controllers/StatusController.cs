using Microsoft.AspNetCore.Mvc;
using OtantikPos.Node.Infrastructure.DeliverySystem;

namespace OtantikPos.Node.Api.Controllers;

// What a till needs to know about this machine on (re)connecting; changes after that arrive on
// the till hub.
[ApiController]
[Route("api/status")]
public sealed class StatusController(DeliverySystemLink link) : ControllerBase
{
    [HttpGet]
    public NodeStatus Get() => new(link.Current);
}

public sealed record NodeStatus(DeliverySystemLinkStatus DeliverySystem);
