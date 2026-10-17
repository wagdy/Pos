import { signal } from '@angular/core';
import { ErrorStateMatcher } from '@angular/material/core';
import { NotifyService } from '../../core/ui/notify.service';

// What stops a form being sent, said where it is and pointing at its field: "Invoice 2, line 3:
// enter the quantity", the field the one whose accessible name is "Invoice 2, Quantity, line 3".
export interface FormProblem {
  where: string;
  what: string;
  field: string;
}

// A send button that is never greyed out: clicked with something missing, nothing is sent, every
// field that holds it up turns red with what it needs under it, the message says what is missing,
// and the first of them is focused. A field stays marked until it is put right; clear() unmarks
// them all, for a form started again.
export class FormChecks {
  // Set once a send is refused for what is missing.
  readonly shown = signal(false);
  private readonly matchers = new Map<string, ErrorStateMatcher>();

  constructor(private readonly problems: () => FormProblem[]) {}

  // How the field named so shows an error: while shown and something is wrong with it.
  field(name: string): ErrorStateMatcher {
    let matcher = this.matchers.get(name);
    if (!matcher) {
      matcher = { isErrorState: () => this.shown() && this.problems().some((p) => p.field === name) };
      this.matchers.set(name, matcher);
    }
    return matcher;
  }

  // What is wrong with the field named so, said under it: "Enter the quantity".
  message(name: string): string {
    const what = this.problems().find((p) => p.field === name)?.what ?? '';
    return what.charAt(0).toUpperCase() + what.slice(1);
  }

  // True when there is nothing missing. Otherwise the fields are marked, the first one is focused
  // (within host), and the message names the first few problems.
  passed(notify: NotifyService, host: HTMLElement): boolean {
    const problems = this.problems();
    if (!problems.length) {
      return true;
    }
    this.shown.set(true);
    const told = problems.slice(0, 3).map((p) => `${p.where}: ${p.what}.`);
    const more = problems.length - told.length;
    notify.error(null, `Cannot submit. ${told.join(' ')}${more ? ` And ${more} more, marked in red.` : ''}`);
    const first = problems.find((p) => p.field)?.field;
    const element = first ? host.querySelector<HTMLElement>(`[aria-label="${first.replace(/["\\]/g, '\\$&')}"]`) : null;
    element?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
    element?.focus({ preventScroll: true });
    return false;
  }

  clear(): void {
    this.shown.set(false);
  }
}
