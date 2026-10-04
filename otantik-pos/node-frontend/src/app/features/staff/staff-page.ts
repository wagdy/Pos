import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { firstValueFrom } from 'rxjs';
import { UserRole } from '../../core/api/models';
import { errorMessage } from '../../core/http/error-message';
import { StaffApi, TillStaff } from '../../core/staff/staff.api';
import { NotifyService } from '../../core/ui/notify.service';
import { PinDialog } from './pin-dialog';

// Who can sign in to this till, and their PINs. The accounts come from the delivery system's
// Staff tab, and arrive here with the menu; a manager gives each one a PIN here. Nobody can sign
// in without one.
@Component({
  selector: 'app-staff-page',
  imports: [MatButtonModule, MatCardModule, MatIconModule, MatProgressBarModule],
  templateUrl: './staff-page.html',
  styleUrl: './staff-page.scss',
})
export class StaffPage {
  private readonly api = inject(StaffApi);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);

  protected readonly staff = signal<TillStaff[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  // Who still cannot sign in comes first.
  protected readonly sorted = computed(() =>
    [...this.staff()].sort((a, b) => Number(a.hasPin) - Number(b.hasPin) || a.fullName.localeCompare(b.fullName)),
  );

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.staff.set(await firstValueFrom(this.api.all()));
    } catch (error) {
      this.error.set(errorMessage(error, 'The staff list could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  protected async setPin(member: TillStaff): Promise<void> {
    const pin = await firstValueFrom(this.dialog.open(PinDialog, { data: member }).afterClosed());
    if (!pin) {
      return;
    }
    try {
      await firstValueFrom(this.api.setPin(member.id, pin));
      this.staff.update((all) => all.map((s) => (s.id === member.id ? { ...s, hasPin: true } : s)));
      this.notify.info(`${member.fullName} can sign in with the new PIN.`);
    } catch (error) {
      this.notify.error(error, 'The PIN could not be saved.');
    }
  }

  protected roleLabel(role: UserRole): string {
    switch (role) {
      case 'CaptainOrder':
        return 'Captain';
      case 'DeliveryCaptain':
        return 'Driver';
      default:
        return role;
    }
  }
}
