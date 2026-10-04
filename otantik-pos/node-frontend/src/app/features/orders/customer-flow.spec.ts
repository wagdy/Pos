import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogConfig, MatDialogRef } from '@angular/material/dialog';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CustomerLookupResult, OrderType } from '../../core/api/models';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { CustomersApi } from '../../core/orders/orders.api';
import { OrdersStore } from '../../core/orders/orders.store';
import { anOrder } from '../../../testing/fixtures';
import { CustomerDetails, CustomerDialog } from './customer-dialog';
import { NewOrderDialog } from './new-order-dialog';
import { OpenOrdersPage } from './open-orders-page';

const found: CustomerLookupResult = {
  status: 'Found',
  profile: { userId: 'customer-1', fullName: 'Ali Hassan', phoneNumber: '01001234567', pointsBalance: 500 },
  redemptionValuePer100Points: 10,
  pointsValue: 50,
};

// Requirement: choosing Takeaway or Delivery always brings up the customer's mobile-number
// dialog, which cannot be dismissed, and with the cloud up the number is looked up before the
// order goes ahead.
describe('customer identification', () => {
  function online(isOnline: boolean): ConnectivityService {
    const connectivity = TestBed.inject(ConnectivityService);
    connectivity.hub.set('connected');
    connectivity.setCloud({
      state: isOnline ? 'Online' : 'Offline',
      sinceUtc: '2026-10-03T12:00:00Z',
      cloudFeaturesAvailable: isOnline,
    });
    return connectivity;
  }

  describe('the new-order flow', () => {
    const opened: { component: unknown; config?: MatDialogConfig }[] = [];
    let answers: unknown[];
    const create = vi.fn();

    beforeEach(() => {
      opened.length = 0;
      create.mockReset().mockResolvedValue(anOrder());
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          provideRouter([]),
          {
            provide: MatDialog,
            useValue: {
              open: (component: unknown, config?: MatDialogConfig) => {
                opened.push({ component, config });
                return { afterClosed: () => of(answers.shift()) };
              },
            },
          },
          { provide: OrdersStore, useValue: { open: signal([]), arrived: signal(new Set()), loaded: signal(true), create } },
        ],
      });
      vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    });

    async function startNewOrder(...dialogAnswers: unknown[]): Promise<void> {
      answers = dialogAnswers;
      const page = TestBed.createComponent(OpenOrdersPage).componentInstance;
      await page['newOrder']();
    }

    for (const type of ['Takeaway', 'Delivery'] as OrderType[]) {
      it(`asks for the mobile number on every ${type.toLowerCase()}, in a dialog that cannot be dismissed`, async () => {
        await startNewOrder({ type }, undefined);

        expect(opened.map((o) => o.component)).toEqual([NewOrderDialog, CustomerDialog]);
        expect(opened[1].config?.disableClose).toBe(true);
        expect(opened[1].config?.data).toEqual({ type });
        // Cancelled at the number: no order.
        expect(create).not.toHaveBeenCalled();
      });
    }

    it('opens the order with the number the dialog took', async () => {
      const customer: CustomerDetails = {
        customerPhone: '01001234567',
        customerName: 'Ali Hassan',
        deliveryAddress: null,
        deliveryFee: 0,
        lookup: found,
      };
      await startNewOrder({ type: 'Takeaway' }, customer);

      expect(create).toHaveBeenCalledWith(
        expect.objectContaining({ type: 'Takeaway', customerPhone: '01001234567', customerName: 'Ali Hassan' }),
      );
    });

    it('does not ask a dine-in table for a number', async () => {
      await startNewOrder({ type: 'DineIn', tableNumber: '7' });

      expect(opened.map((o) => o.component)).toEqual([NewOrderDialog]);
      expect(create).toHaveBeenCalledWith(expect.objectContaining({ type: 'DineIn', tableNumber: '7', customerPhone: null }));
    });
  });

  describe('the mobile-number dialog', () => {
    const close = vi.fn();
    const findByPhone = vi.fn();

    function open(type: OrderType = 'Takeaway'): CustomerDialog {
      TestBed.configureTestingModule({
        providers: [
          provideHttpClient(),
          provideHttpClientTesting(),
          { provide: MAT_DIALOG_DATA, useValue: { type } },
          { provide: MatDialogRef, useValue: { close } },
          { provide: CustomersApi, useValue: { findByPhone } },
        ],
      });
      return TestBed.createComponent(CustomerDialog).componentInstance;
    }

    beforeEach(() => {
      close.mockReset();
      findByPhone.mockReset().mockReturnValue(of(found));
    });

    it('with the cloud up, goes on only once the number has been looked up', async () => {
      const dialog = open();
      online(true);

      dialog['form'].controls.phone.setValue('01001234567');
      expect(dialog['canContinue']()).toBe(false);

      await dialog['lookUp']();
      expect(findByPhone).toHaveBeenCalledWith('01001234567');
      expect(dialog['form'].controls.name.value).toBe('Ali Hassan');
      expect(dialog['canContinue']()).toBe(true);

      dialog['confirm']();
      expect(close).toHaveBeenCalledWith(expect.objectContaining({ customerPhone: '01001234567', lookup: found }));
    });

    it('needs a new lookup when the number changes', async () => {
      const dialog = open();
      online(true);
      dialog['form'].controls.phone.setValue('01001234567');
      await dialog['lookUp']();

      dialog['form'].controls.phone.setValue('01009999999');
      dialog['phoneEdited']();
      expect(dialog['canContinue']()).toBe(false);
    });

    it('with the cloud down, still insists on a number, and keeps it without a lookup', () => {
      const dialog = open();
      online(false);

      expect(dialog['canContinue']()).toBe(false);
      dialog['form'].controls.phone.setValue('01001234567');
      expect(dialog['canContinue']()).toBe(true);
      expect(findByPhone).not.toHaveBeenCalled();
    });

    it('for a delivery, also needs the address', () => {
      const dialog = open('Delivery');
      online(false);

      dialog['form'].controls.phone.setValue('01001234567');
      expect(dialog['canContinue']()).toBe(false);
      dialog['form'].controls.address.setValue('12 Nile St');
      expect(dialog['canContinue']()).toBe(true);
    });
  });
});
