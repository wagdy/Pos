import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormControl, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { WesternDigits, westernDigits } from './western-digits';

// A price as ngModel, a quantity as a form control, a table number, a supplier's invoice number,
// and a name: what the till's screens have.
@Component({
  imports: [FormsModule, ReactiveFormsModule],
  template: `
    <input id="price" type="number" step="any" [(ngModel)]="price" />
    <input id="quantity" type="number" step="any" [formControl]="quantity" />
    <input id="table" inputmode="numeric" [formControl]="table" />
    <input id="invoice" data-western-digits [(ngModel)]="invoiceNumber" />
    <input id="name" [formControl]="name" />
  `,
})
class Fields {
  price: number | null = null;
  invoiceNumber = '';
  readonly quantity = new FormControl<number | null>(null, Validators.min(1));
  readonly table = new FormControl('', { nonNullable: true });
  readonly name = new FormControl('', { nonNullable: true });
}

describe('Arabic digits typed into the till', () => {
  it('are the Western digits, and the Arabic decimal mark a dot', () => {
    expect(westernDigits('٠١٢٣٤٥٦٧٨٩')).toBe('0123456789');
    expect(westernDigits('۰۱۲۳۴۵۶۷۸۹')).toBe('0123456789');
    expect(westernDigits('١٢٫٥')).toBe('12.5');
    expect(westernDigits('بيبسي 1 لتر')).toBe('بيبسي 1 لتر');
  });

  describe('in a field that takes numbers', () => {
    async function render() {
      TestBed.inject(WesternDigits).install();
      const fixture = TestBed.createComponent(Fields);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      const field = (id: string) => el.querySelector<HTMLInputElement>(`#${id}`)!;
      return { fixture, fields: fixture.componentInstance, field };
    }

    // What a key going down does, as the browser sends it: prevented, the key typed nothing.
    function press(field: HTMLInputElement, key: string, init: KeyboardEventInit = {}): boolean {
      field.focus();
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
      field.dispatchEvent(event);
      return event.defaultPrevented;
    }

    function paste(field: HTMLInputElement, text: string): boolean {
      field.focus();
      const event = new Event('paste', { bubbles: true, cancelable: true });
      Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => (type === 'text/plain' ? text : '') } });
      field.dispatchEvent(event);
      return event.defaultPrevented;
    }

    it('goes in as Western digits, and the ngModel and form control take them', async () => {
      const { fixture, fields, field } = await render();

      expect([press(field('price'), '١'), press(field('price'), '٢')]).toEqual([true, true]);
      await fixture.whenStable();
      expect([field('price').value, fields.price]).toEqual(['12', 12]);

      press(field('quantity'), '٥');
      expect([field('quantity').value, fields.quantity.value, fields.quantity.valid]).toEqual(['5', 5, true]);
      press(field('table'), '٩');
      expect(fields.table.value).toBe('9');
    });

    it('goes in where the cursor is, in a text field that takes numbers', async () => {
      const { fields, field } = await render();
      fields.table.setValue('19');
      field('table').setSelectionRange(1, 1);

      press(field('table'), '٥');

      expect(fields.table.value).toBe('159');
      expect(field('table').selectionStart).toBe(2);
    });

    it('is pasted as Western digits, the decimal mark a dot', async () => {
      const { fixture, fields, field } = await render();

      expect(paste(field('quantity'), '١٢٫٥')).toBe(true);
      expect(fields.quantity.value).toBe(12.5);
      paste(field('invoice'), 'ف-١٢');
      await fixture.whenStable();
      expect(fields.invoiceNumber).toBe('ف-12');
      // Western digits already: the paste goes ahead as it is.
      expect(paste(field('price'), '42')).toBe(false);
    });

    it('leaves names, shortcuts and other keys as they are', async () => {
      const { field } = await render();

      expect(press(field('name'), '٣')).toBe(false);
      expect(paste(field('name'), 'بيبسي ١ لتر')).toBe(false);
      expect(press(field('price'), '٣', { ctrlKey: true })).toBe(false);
      expect(press(field('price'), '٣', { metaKey: true })).toBe(false);
      expect(press(field('price'), '3')).toBe(false);
      expect(press(field('price'), 'Backspace')).toBe(false);
    });

    it('leaves a field that cannot be changed alone', async () => {
      const { fields, field } = await render();
      fields.table.disable();
      field('quantity').readOnly = true;

      expect(press(field('quantity'), '٣')).toBe(false);
      expect(press(field('table'), '٣')).toBe(false);
    });
  });
});
