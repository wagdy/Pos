using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Otantik.SharedKernel.Catalog;

namespace OtantikPos.Node.Infrastructure.Persistence.Configurations;

// The menu, copied down from the delivery system (see DeliverySystem/ReferenceDataSync). Ids are
// the delivery system's own, never generated here, so a menu item is the same number on both
// sides: what orders, recipes and stock movements refer to.

internal sealed class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> builder)
    {
        builder.ToTable("MenuItems", NodeDbContext.CatalogSchema);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Ignore(m => m.OrderItems);

        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.NameAr).HasMaxLength(200);
        builder.Property(m => m.Description).HasMaxLength(1000);
        builder.Property(m => m.Category).HasMaxLength(100);
        builder.Property(m => m.ImageUrl).HasMaxLength(2048);
        builder.Property(m => m.PriceNote).HasMaxLength(150);

        builder.HasOne(m => m.SubCategory)
            .WithMany(s => s.MenuItems)
            .HasForeignKey(m => m.SubCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(m => m.Variants)
            .WithOne(v => v.MenuItem)
            .HasForeignKey(v => v.MenuItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(m => m.MenuItemAddOns)
            .WithOne(ma => ma.MenuItem)
            .HasForeignKey(ma => ma.MenuItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MenuItemVariantConfiguration : IEntityTypeConfiguration<MenuItemVariant>
{
    public void Configure(EntityTypeBuilder<MenuItemVariant> builder)
    {
        builder.ToTable("MenuItemVariants", NodeDbContext.CatalogSchema);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).HasMaxLength(100).IsRequired();
        builder.Property(v => v.NameAr).HasMaxLength(100);
    }
}

internal sealed class AddOnConfiguration : IEntityTypeConfiguration<AddOn>
{
    public void Configure(EntityTypeBuilder<AddOn> builder)
    {
        builder.ToTable("AddOns", NodeDbContext.CatalogSchema);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Name).HasMaxLength(100).IsRequired();
        builder.Property(a => a.NameAr).HasMaxLength(100);
    }
}

internal sealed class MenuItemAddOnConfiguration : IEntityTypeConfiguration<MenuItemAddOn>
{
    public void Configure(EntityTypeBuilder<MenuItemAddOn> builder)
    {
        builder.ToTable("MenuItemAddOns", NodeDbContext.CatalogSchema);
        builder.HasKey(ma => new { ma.MenuItemId, ma.AddOnId });

        builder.HasOne(ma => ma.AddOn)
            .WithMany(a => a.MenuItemAddOns)
            .HasForeignKey(ma => ma.AddOnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories", NodeDbContext.CatalogSchema);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.NameAr).HasMaxLength(100);
        builder.Property(c => c.ImageUrl).HasMaxLength(2048);
    }
}

internal sealed class SubCategoryConfiguration : IEntityTypeConfiguration<SubCategory>
{
    public void Configure(EntityTypeBuilder<SubCategory> builder)
    {
        builder.ToTable("SubCategories", NodeDbContext.CatalogSchema);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.NameAr).HasMaxLength(100);

        builder.HasOne(s => s.Category)
            .WithMany()
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RestaurantSettingsCopyConfiguration : IEntityTypeConfiguration<RestaurantSettingsCopy>
{
    public void Configure(EntityTypeBuilder<RestaurantSettingsCopy> builder)
    {
        builder.ToTable("RestaurantSettings", NodeDbContext.CatalogSchema);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.TaxPercentage).HasPrecision(5, 2);
    }
}
