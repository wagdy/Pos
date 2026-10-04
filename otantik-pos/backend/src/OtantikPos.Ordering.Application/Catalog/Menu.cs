using MediatR;
using Otantik.BuildingBlocks;
using Otantik.SharedKernel.Authorization;
using Otantik.SharedKernel.Catalog;
using OtantikPos.Ordering.Application.Ports;

namespace OtantikPos.Ordering.Application.Catalog;

// The menu the till takes orders from: the delivery system's own catalog entities as replicated
// down, so an item, its variants and its add-ons have the same ids and JSON here as in the
// delivery app. Flat lists; the till groups them by CategoryId and SubCategoryId.
//
// Items switched off for now (IsAvailable false) are included, so the till can show them as
// sold out rather than have them vanish mid-shift. Deleted ones are not.
public sealed record TillMenu(
    IReadOnlyList<Category> Categories,
    IReadOnlyList<SubCategory> SubCategories,
    IReadOnlyList<MenuItem> MenuItems);

public sealed record GetMenuQuery : IRequest<TillMenu>;

internal sealed class GetMenuHandler(ICatalog catalog, ICurrentUser user) : IRequestHandler<GetMenuQuery, TillMenu>
{
    public Task<TillMenu> Handle(GetMenuQuery request, CancellationToken cancellationToken)
    {
        if (!user.Has(Permissions.OrderCreate))
            throw new ForbiddenException("You are not allowed to take orders.");

        return catalog.GetMenuAsync(cancellationToken);
    }
}
