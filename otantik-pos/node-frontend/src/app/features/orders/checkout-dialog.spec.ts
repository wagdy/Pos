import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { anOrder } from '../../../testing/fixtures';
import { CheckoutDialog } from './checkout-dialog';

describe('the payment dialog', () => {
  // A captain's round landed while the cashier had it open: it showed L.E 3.41 while the bill
  // behind it was L.E 17.07.
  it('follows the live bill, says it changed, and takes payment for what it shows', async () => {
    const bill = signal(anOrder({ totalAmount: 3.41 }));
    const close = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: { order: bill } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(CheckoutDialog);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).toContain('L.E 3.41');
    expect(element.textContent).not.toContain('The bill changed');

    bill.set(anOrder({ totalAmount: 17.07 }));
    await fixture.whenStable();

    expect(element.querySelector('.amount')?.textContent).toContain('L.E 17.07');
    expect(element.textContent).toContain('The bill changed while this was open: it was L.E 3.41.');

    [...element.querySelectorAll('button')].find((b) => b.textContent?.includes('Take payment'))!.click();
    expect(close).toHaveBeenCalledWith({ method: 'Cash', total: 17.07, cashReceived: null });
  });
});
