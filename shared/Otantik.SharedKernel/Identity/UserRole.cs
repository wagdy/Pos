namespace Otantik.SharedKernel.Identity;

// The coarse role carried in the JWT role claim. What each role may do is in
// Authorization/RolePermissions, not in checks scattered through controllers.
public enum UserRole
{
    Customer,
    Admin,

    // Floor staff taking dine-in orders in the delivery system's app: may create and submit
    // orders, nothing else. No checkout, no final receipt, no void of any kind.
    //
    // Reserved for them alone. Until the delivery system's MoveDriversToDeliveryCaptain
    // migration it was also the drivers' role; they are DeliveryCaptain now.
    CaptainOrder,

    // Shared kernel, appended. Never insert above this line: the role is stored and sent by
    // name, but a client reading numbers would take an inserted value for the next one.
    Cashier,
    Manager,

    // A delivery driver: sees orders and moves them through the delivery leg (OutForDelivery,
    // Delivered).
    DeliveryCaptain
}
