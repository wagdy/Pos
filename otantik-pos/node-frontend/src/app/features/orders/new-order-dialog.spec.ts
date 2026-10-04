import { TestBed } from '@angular/core/testing';
import { MatDialogRef } from '@angular/material/dialog';
import { NewOrderDialog } from './new-order-dialog';

// The dialog itself, clicked as a cashier clicks it: no dialog service mocked around it.
describe('the new-order dialog', () => {
  const close = vi.fn();

  async function render(): Promise<{ element: HTMLElement; settle: () => Promise<void> }> {
    TestBed.configureTestingModule({ providers: [{ provide: MatDialogRef, useValue: { close } }] });
    const fixture = TestBed.createComponent(NewOrderDialog);
    const settle = () => fixture.whenStable();
    await settle();
    return { element: fixture.nativeElement as HTMLElement, settle };
  }

  function button(element: HTMLElement, text: string): HTMLButtonElement {
    const found = [...element.querySelectorAll('button')].find((b) => b.textContent?.trim().endsWith(text));
    if (!found) {
      throw new Error(`No "${text}" button`);
    }
    return found;
  }

  beforeEach(() => close.mockReset());

  // The bug: Open table submitted the form the browser's way and reloaded the page, so no
  // dine-in order was ever opened.
  it('opens a dine-in table from the Open table button, without the browser submitting the page', async () => {
    const { element, settle } = await render();
    button(element, 'Dine-in').click();
    await settle();

    const input = element.querySelector('input')!;
    input.value = '7';
    input.dispatchEvent(new Event('input'));

    let browserSubmitStopped: boolean | undefined;
    const watch = (event: Event) => (browserSubmitStopped = event.defaultPrevented);
    document.addEventListener('submit', watch);
    try {
      button(element, 'Open table').click();
      await settle();
    } finally {
      document.removeEventListener('submit', watch);
    }

    expect(browserSubmitStopped).toBe(true);
    expect(close).toHaveBeenCalledWith({ type: 'DineIn', tableNumber: '7' });
  });

  it('insists on a table number', async () => {
    const { element, settle } = await render();
    button(element, 'Dine-in').click();
    await settle();

    button(element, 'Open table').click();
    await settle();

    expect(close).not.toHaveBeenCalled();
    expect(element.textContent).toContain('Enter the table number.');
  });

  it('hands takeaway straight on, for the customer number to be asked next', async () => {
    const { element } = await render();
    button(element, 'Takeaway').click();

    expect(close).toHaveBeenCalledWith({ type: 'Takeaway' });
  });
});
