import { Pipe, PipeTransform } from '@angular/core';

const formatter = new Intl.NumberFormat('en-EG', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

// Amounts as the delivery app shows them: "L.E 153.90".
@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    return `L.E ${formatter.format(value ?? 0)}`;
  }
}
