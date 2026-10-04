namespace Otantik.SharedKernel.Orders;

// Cash and Instapay orders default straight to Confirmed - neither is verified by this
// system (cash is collected on delivery, Instapay is a manual bank transfer the admin
// reconciles using the InstapayAccount details shown at checkout). Visa is the only
// method that starts Pending, since placing the order only redirects the customer to
// the admin-configured Fawry link - there's no payment gateway webhook integration here
// to ever flip it automatically, so a Pending Visa order is a signal for an admin to
// confirm payment manually once received.
//
// Confirmed is deliberately listed first (CLR default value 0), matching
// Order.PaymentStatus's own C# default - EF's database-generated-default handling
// can't tell "explicitly set to the enum's zero value" apart from "left unset" for
// whichever member is 0, so Pending (the one value that's ever explicitly assigned)
// must not be that member, or an explicit Pending could get silently overwritten by
// the column's DB default on insert.
public enum PaymentStatus
{
    Confirmed,
    Pending,

    // Shared kernel, appended for Void After. Never insert above this line; see OrderStatus.
    //
    // An open POS bill is Pending until checkout. The C# default stays Confirmed, which is
    // what the delivery system's cash-on-delivery orders rely on, so the POS sets Pending
    // explicitly when it opens a bill.
    PartiallyRefunded,
    Refunded
}
