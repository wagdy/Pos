import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { UserRole } from '../api/models';

// Someone who may use the till. The account itself (name, role, active) is the delivery
// system's; the PIN is this till's alone. IsLocalOnly marks the first manager this machine
// created for itself, from its configuration.
export interface TillStaff {
  id: string;
  fullName: string;
  role: UserRole;
  hasPin: boolean;
  isLocalOnly: boolean;
}

// Till PINs, for managers (StaffManage).
@Injectable({ providedIn: 'root' })
export class StaffApi {
  private readonly http = inject(HttpClient);

  all(): Observable<TillStaff[]> {
    return this.http.get<TillStaff[]>('/api/staff');
  }

  // Sets or replaces the PIN, and lifts a lockout after too many wrong ones.
  setPin(staffId: string, pin: string): Observable<void> {
    return this.http.put<void>(`/api/staff/${encodeURIComponent(staffId)}/pin`, { pin });
  }
}
