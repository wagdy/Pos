using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Identity;
using OtantikPos.Node.Infrastructure.Persistence;
using OtantikPos.Node.Infrastructure.Persistence.Configurations;

namespace OtantikPos.Node.Infrastructure.Identity;

// A staff member as this till knows them: the account copied from the delivery system (the same
// AppUser id, so the audit trail names the same person in both places), plus a till PIN, which
// exists only here. The PIN is what lets a cashier sign in with the internet down.
public sealed class StaffMember
{
    // The delivery system's AppUser id. "local:..." for the one account a till can create
    // itself (see StaffDirectory.EnsureBootstrapManagerAsync).
    public string Id { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsLocalOnly { get; set; }

    public string? PinHash { get; set; }

    public int FailedSignInAttempts { get; set; }

    public DateTime? LockedUntilUtc { get; set; }
}

internal sealed class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> builder)
    {
        builder.ToTable("Staff", NodeDbContext.IdentitySchema);
        builder.Property(s => s.Id).HasMaxLength(450);
        builder.Property(s => s.FullName).HasMaxLength(200);
        builder.Property(s => s.Role).IsEnumName();
        builder.Property(s => s.PinHash).HasMaxLength(200);
    }
}

public sealed record SignInOutcome(StaffMember? Staff, DateTime? LockedUntilUtc);

// PIN sign-in at the till. Accounts and roles come from the delivery system; this holds only
// the PINs and the lockout.
public sealed class StaffDirectory(NodeDbContext db, ILogger<StaffDirectory> logger)
{
    // Who may sign in to a till. Captains take orders in the delivery app, not here.
    // An array, not a set: EF translates Contains on an array into SQL, and not on IReadOnlySet.
    private static readonly UserRole[] TillRoles = [UserRole.Cashier, UserRole.Manager, UserRole.Admin];

    // A 4-digit PIN has only 10,000 possibilities, so guessing has to be slow: five misses lock
    // the account for five minutes.
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private static readonly PasswordHasher<StaffMember> Hasher = new();

    // The sign-in screen: active till staff who have a PIN.
    public async Task<IReadOnlyList<StaffMember>> GetSignInListAsync(CancellationToken cancellationToken) =>
        await db.Staff.AsNoTracking()
            .Where(s => s.IsActive && s.PinHash != null && TillRoles.Contains(s.Role))
            .OrderBy(s => s.FullName)
            .ToListAsync(cancellationToken);

    // For a manager setting PINs: every active till account, with or without one yet.
    public async Task<IReadOnlyList<StaffMember>> GetTillStaffAsync(CancellationToken cancellationToken) =>
        await db.Staff.AsNoTracking()
            .Where(s => s.IsActive && TillRoles.Contains(s.Role))
            .OrderBy(s => s.FullName)
            .ToListAsync(cancellationToken);

    // Null when the account is gone, deactivated, or not a till role. Checked on every request,
    // so a deactivation in the delivery system reaches the till at its next sync rather than
    // when a token expires.
    public Task<UserRole?> GetActiveRoleAsync(string staffId, CancellationToken cancellationToken) =>
        db.Staff.AsNoTracking()
            .Where(s => s.Id == staffId && s.IsActive && TillRoles.Contains(s.Role))
            .Select(s => (UserRole?)s.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<SignInOutcome> SignInAsync(string staffId, string pin, CancellationToken cancellationToken)
    {
        var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == staffId && s.IsActive, cancellationToken);
        if (staff?.PinHash is null || !TillRoles.Contains(staff.Role))
            return new SignInOutcome(null, null);
        if (staff.LockedUntilUtc > DateTime.UtcNow)
            return new SignInOutcome(null, staff.LockedUntilUtc);

        var result = Hasher.VerifyHashedPassword(staff, staff.PinHash, pin);
        if (result == PasswordVerificationResult.Failed)
        {
            if (++staff.FailedSignInAttempts >= MaxFailedAttempts)
            {
                staff.FailedSignInAttempts = 0;
                staff.LockedUntilUtc = DateTime.UtcNow + LockoutDuration;
                logger.LogWarning("Staff account {StaffId} locked after {Attempts} wrong PINs", staff.Id, MaxFailedAttempts);
            }
            await db.SaveChangesAsync(cancellationToken);
            return new SignInOutcome(null, staff.LockedUntilUtc);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            staff.PinHash = Hasher.HashPassword(staff, pin);
        staff.FailedSignInAttempts = 0;
        staff.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return new SignInOutcome(staff, null);
    }

    // Also clears a lockout: a manager resetting the PIN is how a locked-out cashier gets back
    // in before the five minutes are up.
    public async Task SetPinAsync(string staffId, string pin, CancellationToken cancellationToken)
    {
        var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == staffId, cancellationToken)
            ?? throw new NotFoundException("Staff member", staffId);
        if (pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
            throw new DomainException("A PIN is 4 to 8 digits.");

        staff.PinHash = Hasher.HashPassword(staff, pin);
        staff.FailedSignInAttempts = 0;
        staff.LockedUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // First run on a machine that has never synced: with no staff at all, nobody could sign in
    // to set anyone's PIN. So the first manager comes from configuration, as a local-only
    // account. Once the delivery system's staff arrive and someone there has a PIN, it can be
    // deactivated.
    public async Task EnsureBootstrapManagerAsync(string? name, string? pin, CancellationToken cancellationToken)
    {
        if (await db.Staff.AnyAsync(s => s.PinHash != null, cancellationToken))
            return;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pin))
        {
            logger.LogWarning("No staff member on this till has a PIN. Set Auth:BootstrapManager:Name and Pin to create a first manager.");
            return;
        }

        db.Staff.Add(new StaffMember { Id = $"local:{Guid.NewGuid()}", FullName = name.Trim(), Role = UserRole.Manager, IsLocalOnly = true });
        await db.SaveChangesAsync(cancellationToken);

        var created = await db.Staff.SingleAsync(s => s.IsLocalOnly && s.FullName == name.Trim(), cancellationToken);
        await SetPinAsync(created.Id, pin, cancellationToken);
        logger.LogWarning("Created a local manager, {Name}, from Auth:BootstrapManager. Remove the PIN from configuration now.", name);
    }
}
