namespace OtantikPos.Inventory.Domain.RawMaterials;

// Base units only. A 5 kg delivery is booked as 5000 g, so a recipe, a delivery and a stock
// count can never disagree about what "1" means.
public enum UnitOfMeasure
{
    Gram = 1,
    Millilitre = 2,
    Piece = 3,
}
