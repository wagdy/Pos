import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Order } from '../api/models';
import { anOrder, cashierMe } from '../../../testing/fixtures';
import { AuthService } from '../auth/auth.service';
import { OrdersApi } from './orders.api';
import { OrdersStore } from './orders.store';

describe('OrdersStore', () => {
  let store: OrdersStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        { provide: OrdersApi, useValue: {} },
        { provide: AuthService, useValue: { me: signal(cashierMe) } },
      ],
    });
    store = TestBed.inject(OrdersStore);
  });

  // Requirement: a captain's dine-in order from the delivery app appears on the open-orders
  // screen the moment the hub pushes it, with no refresh, and stands out as new.
  it("puts a pushed order it has never seen on the floor at once, as an arrival", () => {
    const arrivals: Order[] = [];
    store.onArrival = (order) => arrivals.push(order);

    const mine = anOrder({ tableNumber: '4', createdAt: '2026-10-03T12:05:00Z' });
    store.receive(mine, 'local');
    const captains = anOrder({ tableNumber: '9', createdByUserId: 'staff-captain', createdAt: '2026-10-03T12:01:00Z' });
    store.receive(captains, 'push');

    expect(store.open().map((o) => o.tableNumber)).toEqual(['9', '4']);
    expect(arrivals).toEqual([captains]);
    expect(store.arrived().has(captains.publicId)).toBe(true);
    expect(store.arrived().has(mine.publicId)).toBe(false);

    store.acknowledge(captains.publicId);
    expect(store.arrived().size).toBe(0);
  });

  // The push for a takeaway the cashier just opened can come back before the response does.
  it("does not announce the signed-in person's own order as an arrival", () => {
    const arrivals: Order[] = [];
    store.onArrival = (order) => arrivals.push(order);

    const mine = anOrder({ createdByUserId: cashierMe.id });
    store.receive(mine, 'push');

    expect(store.open()).toEqual([mine]);
    expect(arrivals).toEqual([]);
    expect(store.arrived().size).toBe(0);
  });

  it('takes a paid order off the floor, and an overtaken push cannot bring it back', () => {
    const order = anOrder({ updatedAt: '2026-10-03T12:00:05.5Z' });
    store.receive(order, 'local');
    store.receive(
      { ...order, closedAt: '2026-10-03T12:00:06Z', paymentStatus: 'Confirmed', status: 'Served', updatedAt: '2026-10-03T12:00:06Z' },
      'push',
    );
    expect(store.open()).toEqual([]);

    store.receive({ ...order, updatedAt: '2026-10-03T12:00:05.9Z' }, 'push');
    expect(store.open()).toEqual([]);
    expect(store.find(order.publicId)?.status).toBe('Served');
  });

  // The API writes as many fractional digits as a time has: "…:06Z" and "…:06.5Z". Compared as
  // strings, the later one would lose.
  it('compares change times as times, whatever their precision', () => {
    const order = anOrder({ updatedAt: '2026-10-03T12:00:06Z' });
    store.receive(order, 'local');
    store.receive({ ...order, tableNumber: '12', updatedAt: '2026-10-03T12:00:06.5Z' }, 'push');

    expect(store.find(order.publicId)?.tableNumber).toBe('12');
  });
});
