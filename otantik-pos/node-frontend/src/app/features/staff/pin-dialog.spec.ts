import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { TillStaff } from '../../core/staff/staff.api';
import { PinDialog } from './pin-dialog';

describe('the PIN dialog', () => {
  const sara: TillStaff = { id: 'staff-cashier', fullName: 'Sara', role: 'Cashier', hasPin: true, isLocalOnly: false };
  const close = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: sara },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(PinDialog);
    await fixture.whenStable();
    const element = fixture.nativeElement as HTMLElement;
    const [pin, repeat] = [...element.querySelectorAll('input')];
    const save = [...element.querySelectorAll('button')].find((b) => b.textContent?.includes('Save PIN'))!;

    // Typed, then left, as a manager moving on to the next field or to Save PIN.
    async function type(input: HTMLInputElement, value: string) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new Event('blur'));
      await fixture.whenStable();
    }

    return { element, pin: pin!, repeat: repeat!, save, type };
  }

  beforeEach(() => close.mockReset());

  it('says why it cannot save when the two PINs differ', async () => {
    const { element, pin, repeat, save, type } = await render();

    await type(pin, '2468');
    await type(repeat, '2469');

    expect(element.textContent).toContain('The two PINs are not the same.');
    expect(save.disabled).toBe(true);
  });

  it('gives back the PIN when both are the same', async () => {
    const { element, pin, repeat, save, type } = await render();

    await type(pin, '2468');
    await type(repeat, '2468');
    save.click();

    expect(element.textContent).not.toContain('The two PINs are not the same.');
    expect(close).toHaveBeenCalledWith('2468');
  });
});
