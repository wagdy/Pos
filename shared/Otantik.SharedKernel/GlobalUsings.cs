// The model's namespaces reference each other (an Order has an AppUser and MenuItems, a MenuItem
// has OrderItems), exactly as they did when they were all one namespace in the delivery system.
global using Otantik.SharedKernel.Catalog;
global using Otantik.SharedKernel.Customers;
global using Otantik.SharedKernel.Identity;
global using Otantik.SharedKernel.Orders;
global using Otantik.SharedKernel.Authorization;
