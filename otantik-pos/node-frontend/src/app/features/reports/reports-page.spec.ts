import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { DayReport } from '../../core/api/models';
import { OrdersApi } from '../../core/orders/orders.api';
import { OrdersStore } from '../../core/orders/orders.store';
import { ReportsApi } from '../../core/reports/reports.api';
import { anOrder } from '../../../testing/fixtures';
import { ReportsPage } from './reports-page';

function aReport(businessDate: string, netSales: number): DayReport {
  const order = anOrder({ closedAt: '2026-10-03T12:30:00Z', paymentStatus: 'Confirmed', status: 'Served', totalAmount: netSales });
  return {
    businessDate,
    fromUtc: '',
    toUtc: '',
    summary: {
      orders: 1,
      paidOrders: 1,
      openOrders: 0,
      openValue: 0,
      cancelledOrders: 0,
      grossSales: netSales,
      refunds: 0,
      netSales,
      averageTicket: netSales,
      taxCharged: 0,
      pointsRedeemed: 0,
      pointsDiscount: 0,
      pointsReturned: 0,
      voidedBeforeKitchen: { items: 0, value: 0 },
      voidedAfterKitchen: { items: 0, value: 0 },
    },
    byPaymentMethod: [{ method: 'Cash', orders: 1, charged: netSales, refunded: 0, net: netSales }],
    byOrderType: [{ type: 'DineIn', orders: 1, net: netSales }],
    orders: [order],
  };
}

describe('the reports page', () => {
  const day = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: ReportsApi, useValue: { day } }, { provide: OrdersApi, useValue: {} }],
    });
    const fixture = TestBed.createComponent(ReportsPage);
    await fixture.whenStable();
    return { fixture, page: fixture.componentInstance, text: () => (fixture.nativeElement as HTMLElement).textContent ?? '' };
  }

  beforeEach(() => {
    day.mockReset().mockImplementation((date: string | null) => of(aReport(date ?? '2026-10-03', date ? 100 : 136.8)));
  });

  it("shows today's takings", async () => {
    const { text } = await render();

    expect(day).toHaveBeenCalledWith(null);
    expect(text()).toContain('L.E 136.80');
    expect(text()).toContain('Saturday 3 October 2026');
  });

  // The cashier's complaint: a paid order did not show up. Today's report reloads itself when
  // any order changes, here or at another till.
  it('reloads today when an order changes anywhere', async () => {
    const { fixture } = await render();
    day.mockClear();

    TestBed.inject(OrdersStore).receive(anOrder(), 'push');
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 1100));

    expect(day).toHaveBeenCalledWith(null);
  });

  it('steps back a day, and leaves a past day alone when orders change', async () => {
    const { fixture, page, text } = await render();

    page['step'](-1);
    await fixture.whenStable();
    expect(day).toHaveBeenLastCalledWith('2026-10-02');
    expect(text()).toContain('L.E 100.00');

    day.mockClear();
    TestBed.inject(OrdersStore).receive(anOrder(), 'push');
    await new Promise((resolve) => setTimeout(resolve, 1100));
    expect(day).not.toHaveBeenCalled();
  });
});
