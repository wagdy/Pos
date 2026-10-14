import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, inject } from '@angular/core';

// Digits typed on an Arabic keyboard (٠١٢٣٤٥٦٧٨٩, or the Persian ۰۱۲۳۴۵۶۷۸۹) and the Arabic decimal
// mark (٫): the Western digits and the dot they stand for. Everything else as it is.
const EASTERN = /[٠-٩۰-۹٫]/g;

export function westernDigits(text: string): string {
  return text.replace(EASTERN, (c) => (c === '٫' ? '.' : String(c.charCodeAt(0) - (c >= '۰' ? 0x06f0 : 0x0660))));
}

// The fields that take numbers: prices, quantities and amounts, PINs, phone numbers and table
// numbers; and any marked data-western-digits, such as a supplier's invoice number, of letters and
// digits. Names, notes and searches keep what is typed.
const NUMBER_FIELDS =
  'input[type=number], input[type=tel], input[inputmode=numeric], input[inputmode=decimal], input[inputmode=tel], input[data-western-digits]';

// So that nobody has to switch keyboards between an Arabic name and its price: an Arabic digit typed
// or pasted into a field that takes numbers goes in as the Western one, wherever the field is in the
// till. Installed once, at startup, for the whole page.
//
// A number field drops an Arabic digit before any input event, so the key is caught as it goes
// down, and the Western digit typed in its place. Typed by the browser's own insert, at the cursor:
// the field's input event follows, so ngModel and form controls take the value, and undo works.
@Injectable({ providedIn: 'root' })
export class WesternDigits {
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private installed = false;

  install(): void {
    if (this.installed) return;
    this.installed = true;
    const listeners: [string, (event: Event) => void][] = [
      ['keydown', (event) => this.keydown(event as KeyboardEvent)],
      ['beforeinput', (event) => this.beforeInput(event as InputEvent)],
      ['paste', (event) => this.paste(event as ClipboardEvent)],
    ];
    for (const [type, listener] of listeners) this.document.addEventListener(type, listener, true);
    this.destroyRef.onDestroy(() => {
      for (const [type, listener] of listeners) this.document.removeEventListener(type, listener, true);
    });
  }

  // A key that types an Arabic digit. Shortcuts (Ctrl, Cmd) are left alone.
  private keydown(event: KeyboardEvent): void {
    if (event.ctrlKey || event.metaKey || event.isComposing || event.key?.length !== 1) return;
    this.replace(event, event.key);
  }

  // Typed some other way into a text field that lets it in: an on-screen keyboard, a suggestion.
  private beforeInput(event: InputEvent): void {
    if (event.inputType === 'insertText' || event.inputType === 'insertReplacementText') this.replace(event, event.data);
  }

  private paste(event: ClipboardEvent): void {
    this.replace(event, event.clipboardData?.getData('text/plain'));
  }

  private replace(event: Event, text: string | null | undefined): void {
    const field = numberField(event.target);
    if (!field || !text || !event.cancelable) return;
    const western = westernDigits(text);
    if (western === text) return;
    event.preventDefault();
    insert(field, western);
  }
}

function numberField(target: EventTarget | null): HTMLInputElement | null {
  return target instanceof HTMLInputElement && !target.readOnly && !target.disabled && target.matches(NUMBER_FIELDS) ? target : null;
}

// At the cursor, as the browser types; or, where it cannot, there by hand (at the end of a number
// field, which keeps no cursor), and the input event raised for it.
function insert(field: HTMLInputElement, text: string): void {
  const document = field.ownerDocument;
  try {
    if (document.activeElement === field && document.execCommand('insertText', false, text)) return;
  } catch {
    // An older browser: by hand, below.
  }
  try {
    field.setRangeText(text, field.selectionStart ?? field.value.length, field.selectionEnd ?? field.value.length, 'end');
  } catch {
    field.value += text;
  }
  field.dispatchEvent(new Event('input', { bubbles: true }));
}
