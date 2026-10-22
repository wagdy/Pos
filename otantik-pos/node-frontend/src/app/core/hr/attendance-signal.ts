import { Injectable, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { EmployeeStatus } from './hr.api';

// What the till says about attendance, for what shows who is in. `statuses`: each employee's card
// as punches or a correction changed it (the hub's EmployeeStatusChanged). `changes`: read it all
// again, as after the connection was lost and pushes may have been missed; counts up, so every
// time is a new value.
@Injectable({ providedIn: 'root' })
export class AttendanceSignal {
  readonly changes = signal(0);
  readonly statuses = new Subject<EmployeeStatus[]>();

  changed(): void {
    this.changes.update((n) => n + 1);
  }

  statusChanged(statuses: EmployeeStatus[]): void {
    this.statuses.next(statuses);
  }
}
