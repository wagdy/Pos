using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Identity;
using OtantikPos.Node.Infrastructure.Identity;

namespace OtantikPos.Node.Api.Controllers;

// Till PINs, for managers. Only PINs: the accounts themselves, their names, roles and whether
// they are active, are the delivery system's and change there.
[ApiController]
[Route("api/staff")]
[Authorize(Policy = Permissions.StaffManage)]
public sealed class StaffController(StaffDirectory staff) : ControllerBase
{
    // Every active account that may use a till, with or without a PIN yet.
    [HttpGet]
    public async Task<IReadOnlyList<TillStaffDto>> GetAll(CancellationToken cancellationToken) =>
        (await staff.GetTillStaffAsync(cancellationToken))
            .Select(s => new TillStaffDto(s.Id, s.FullName, s.Role, s.PinHash is not null, s.IsLocalOnly))
            .ToList();

    // Also lifts a lockout, which is how a locked-out cashier gets back in early.
    [HttpPut("{staffId}/pin")]
    public async Task<IActionResult> SetPin(string staffId, SetPinRequest request, CancellationToken cancellationToken)
    {
        await staff.SetPinAsync(staffId, request.Pin, cancellationToken);
        return NoContent();
    }
}

// IsLocalOnly marks the bootstrap manager, the one account this machine made itself.
public sealed record TillStaffDto(string Id, string FullName, UserRole Role, bool HasPin, bool IsLocalOnly);

public sealed record SetPinRequest(string Pin);
