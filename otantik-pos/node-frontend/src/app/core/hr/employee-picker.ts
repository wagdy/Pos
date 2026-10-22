import { Component, computed, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Employee } from './hr.api';

// A name as two spellings of it are compared: capitals aside, without the stretching tatweel, and
// the Arabic letters written more than one way (أ إ آ ا, ة ه, ى ي) as one. "منى" finds "مني".
export function searchKey(text: string): string {
  return text
    .replace(/[أإآ]/g, 'ا')
    .replace(/ة/g, 'ه')
    .replace(/ى/g, 'ي')
    .replace(/ـ/g, '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

// An employee chosen by typing part of their name or job: the list narrows as it is typed. With
// `none`, no one is a choice too (null): its label says what that means where it is used.
@Component({
  selector: 'app-employee-picker',
  imports: [FormsModule, MatAutocompleteModule, MatFormFieldModule, MatInputModule],
  template: `
    <mat-form-field subscriptSizing="dynamic" class="picker" [class.fill]="fill()">
      <mat-label>{{ label() }}</mat-label>
      <input matInput [ngModel]="text()" (ngModelChange)="typed($event)" [matAutocomplete]="list" [disabled]="disabled()"
        [placeholder]="none() ? noneLabel() : 'Type a name'" [attr.aria-label]="ariaLabel()" />
      <mat-autocomplete #list="matAutocomplete" [displayWith]="display" (optionSelected)="chose($event)" (closed)="settle()">
        @if (none()) {
          <mat-option [value]="null" class="none">{{ noneLabel() }}</mat-option>
        }
        @for (e of shown(); track e.id) {
          <mat-option [value]="e.id">{{ e.name }} <span class="job">· {{ e.jobTitle }}</span></mat-option>
        } @empty {
          <mat-option disabled>{{ employees().length ? 'No one by that name' : 'No employees yet: add them under HR & Payroll' }}</mat-option>
        }
      </mat-autocomplete>
      @if (hint(); as h) {
        <mat-hint>{{ h }}</mat-hint>
      }
    </mat-form-field>
  `,
  styles: `
    :host {
      display: contents;
    }
    .picker {
      min-width: 240px;
    }
    .fill {
      width: 100%;
    }
    .job,
    .none {
      color: var(--mat-sys-on-surface-variant);
    }
  `,
})
export class EmployeePicker {
  readonly employees = input.required<Employee[]>();
  readonly value = model<string | null>(null);
  readonly label = input('Employee · الموظف');
  readonly ariaLabel = input('Employee');
  readonly none = input(false);
  readonly noneLabel = input('No one yet · لم يُحدد بعد');
  readonly hint = input<string | null>(null);
  readonly disabled = input(false);
  // As wide as where it is put, as a form's own fields are.
  readonly fill = input(false);

  // What is typed to narrow the list; empty while the box shows the one chosen.
  private readonly filter = signal<string | null>(null);

  // The box: what is typed, or the one chosen (shown by its name).
  protected readonly text = computed(() => this.filter() ?? this.value());

  protected readonly shown = computed(() => {
    const key = searchKey(this.filter() ?? '');
    return this.employees().filter((e) => !key || searchKey(e.name).includes(key) || searchKey(e.jobTitle).includes(key));
  });

  // An id shown as its employee's name; what is being typed, as typed.
  protected readonly display = (value: string | null): string =>
    value === null ? '' : (this.employees().find((e) => e.id === value)?.name ?? value);

  protected typed(text: string | null): void {
    // The autocomplete writes the chosen option's value too: that is chose()'s.
    if (text !== null && this.employees().some((e) => e.id === text)) {
      return;
    }
    this.filter.set(text ?? '');
    if (this.none() && !(text ?? '').trim()) {
      this.value.set(null);
    }
  }

  protected chose(event: MatAutocompleteSelectedEvent): void {
    this.value.set(event.option.value as string | null);
    this.filter.set(null);
  }

  // The list closed without a choice: the box shows the one chosen again.
  protected settle(): void {
    this.filter.set(null);
  }
}
