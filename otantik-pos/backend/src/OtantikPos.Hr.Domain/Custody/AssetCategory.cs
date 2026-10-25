using Otantik.BuildingBlocks;

namespace OtantikPos.Hr.Domain.Custody;

// A kind of asset in someone's custody (فئة الأصول): tableware, kitchen tools, devices, uniforms.
// Consumable (مستهلك) ones are the fragile and the worn: plates and glasses, whose breakage is a
// cost of the day (routed to Costing → Waste); the rest are fixed assets, a laptop or a fryer.
public sealed class AssetCategory : AggregateRoot
{
    public const int MaxNameLength = 100;

    private AssetCategory() { }

    public AssetCategory(Guid id, string name, bool isConsumable)
    {
        Id = id;
        Rename(name);
        IsConsumable = isConsumable;
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string Name { get; private set; } = string.Empty;
    public bool IsConsumable { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public void Rename(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("Give the category its name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException($"Keep the category's name under {MaxNameLength} characters.");
        Name = trimmed;
    }

    // From now on: breakage already recorded stays as it was.
    public void SetConsumable(bool isConsumable) => IsConsumable = isConsumable;
}
