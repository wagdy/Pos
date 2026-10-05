import { MenuItem } from '../api/models';
import { anItem, anOrder } from '../../../testing/fixtures';
import {
  addItemProblem,
  awaitsPayment,
  isOpen,
  isSettled,
  maxPointsFor,
  pointsDiscount,
  requiresCustomerPhone,
  statusText,
  voidTypeFor,
} from './order-rules';

// The screen's copy of the shared kernel's rules must give the backend's answers, or the till
// offers buttons the API then refuses.
describe('order rules, as the shared kernel has them', () => {
  it('counts an order closed at the till as paid, and a paid order as no longer open', () => {
    const open = anOrder();
    const paid = anOrder({ closedAt: '2026-10-03T12:30:00Z', paymentStatus: 'Confirmed', status: 'Served' });

    expect([isSettled(open), isOpen(open)]).toEqual([false, true]);
    expect([isSettled(paid), isOpen(paid)]).toEqual([true, false]);
    // Cash on delivery is "Confirmed" from the start, but not paid until delivered.
    expect(isSettled(anOrder({ type: 'Delivery', paymentStatus: 'Confirmed', paymentMethod: 'Cash' }))).toBe(false);
  });

  // Cash on delivery: handed over first, paid at the till after.
  it('keeps a delivered order payable, but closed to new items, until its cash is taken', () => {
    const delivered = anOrder({ type: 'Delivery', status: 'Delivered', paymentStatus: 'Pending' });

    expect(isOpen(delivered)).toBe(false);
    expect(awaitsPayment(delivered)).toBe(true);
    expect(statusText(delivered)).toBe('Delivered · not paid');

    const paid = { ...delivered, closedAt: '2026-10-04T10:00:00Z', paymentStatus: 'Confirmed' as const };
    expect(awaitsPayment(paid)).toBe(false);
    expect(statusText(paid)).toBe('Paid');
    expect(awaitsPayment(anOrder({ status: 'Cancelled' }))).toBe(false);
  });

  it('says which kind of void an item would be, from the order alone', () => {
    const unsent = anItem();
    const sent = anItem({ sentToKitchenAt: '2026-10-03T12:01:00Z' });
    const order = anOrder({ orderItems: [unsent, sent] });
    const paid = { ...order, closedAt: '2026-10-03T12:30:00Z', paymentStatus: 'Confirmed' as const };

    expect(voidTypeFor(order, unsent)).toBe('BeforeKitchen');
    expect(voidTypeFor(order, sent)).toBe('AfterKitchen');
    expect(voidTypeFor(paid, sent)).toBe('AfterPayment');
    expect(voidTypeFor({ ...paid, paymentStatus: 'Refunded' }, sent)).toBeNull();
    expect(voidTypeFor(order, anItem({ voidType: 'BeforeKitchen' }))).toBeNull();
  });

  // Points / 10 at the default rate; any rate the delivery system sets, rounded as it rounds.
  it('prices points exactly as both systems do', () => {
    expect(pointsDiscount(15, 10)).toBe(1.5);
    expect(pointsDiscount(400, 20)).toBe(80);
    expect(pointsDiscount(33, 12.5)).toBe(4.13);
    expect(pointsDiscount(400, 0)).toBe(0);
    expect(maxPointsFor(55.55, 10)).toBe(555);
    expect(maxPointsFor(55.5, 10)).toBe(555);
    expect(maxPointsFor(55.55, 12.5)).toBe(444);
  });

  it('asks for the mobile number for takeaway and delivery, not for a table', () => {
    expect(requiresCustomerPhone('Takeaway')).toBe(true);
    expect(requiresCustomerPhone('Delivery')).toBe(true);
    expect(requiresCustomerPhone('DineIn')).toBe(false);
  });

  it('refuses what the API would refuse before the cashier taps Add', () => {
    const shawarma: MenuItem = {
      id: 10,
      name: 'Shawarma',
      nameAr: null,
      description: null,
      price: 0,
      imageUrl: null,
      isAvailable: true,
      isPriceBasedOnAddons: false,
      priceNote: null,
      category: 'Mains',
      subCategoryId: 11,
      menuItemAddOns: [],
      variants: [{ id: 101, menuItemId: 10, name: 'Kilo tray', nameAr: null, price: 400, displayOrder: 0, isAvailable: true }],
    };

    expect(addItemProblem(shawarma, null, [])).toBe("Please choose a size for 'Shawarma'.");
    expect(addItemProblem(shawarma, 101, [])).toBeNull();
    expect(addItemProblem({ ...shawarma, isAvailable: false }, 101, [])).toBe("'Shawarma' is currently unavailable.");
  });
});
