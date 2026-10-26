import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { LeaveRequest } from '../../core/hr/leaves.api';
import { EmployeeLeaves, PortalApi } from '../../core/portal/portal.api';
import { NotifyService } from '../../core/ui/notify.service';
import { LeaveRequestDialog } from '../hr/leave-dialogs';
import { MyLeaves } from './my-leaves';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function request(id: string, over: Partial<LeaveRequest> = {}): LeaveRequest {
  return {
    id, employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'annual', leaveTypeName: 'Annual', isPaid: true, startDate: '2026-11-01',
    endDate: '2026-11-05', days: 5, daysTaken: 5, status: 'Pending', reason: null, managerNotes: null, requestedBy: 'Hassan',
    requestedAtUtc: '2026-10-20T09:00:00Z', decidedBy: null, decidedAtUtc: null, cancelledFrom: null, cancelledDays: 0, cancelledBy: null,
    cancelledAtUtc: null, cancellationNotes: null, ...over,
  };
}

describe('the portal’s My leaves', () => {
  const leaves = vi.fn();
  const leaveDays = vi.fn();
  const requestLeave = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  const theirs: EmployeeLeaves = {
    year: 2026,
    balances: [
      { employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'annual', leaveTypeName: 'Annual', year: 2026, allocatedDays: 10.5, usedDays: 3, remainingDays: 7.5, pendingDays: 5, opened: true, allocatedBy: null },
      { employeeId: 'hassan', employeeName: 'Hassan', leaveTypeId: 'sick', leaveTypeName: 'Sick', year: 2026, allocatedDays: 7, usedDays: 0, remainingDays: 7, pendingDays: 0, opened: false, allocatedBy: null },
    ],
    requests: [
      request('r1'),
      request('r2', { status: 'Approved', startDate: '2026-10-11', endDate: '2026-10-15', cancelledFrom: '2026-10-14', cancelledDays: 2, daysTaken: 3, cancellationNotes: 'Recalled' }),
      request('r3', { status: 'Rejected', startDate: '2026-09-07', endDate: '2026-09-07', days: 1, managerNotes: 'Busy week' }),
    ],
    types: [{ id: 'annual', name: 'Annual', isPaid: true }, { id: 'unpaid', name: 'Unpaid', isPaid: false }],
  };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: PortalApi, useValue: { leaves, leaveDays, requestLeave } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(MyLeaves);
    for (let i = 0; i < 2; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    leaves.mockReset().mockReturnValue(of(theirs));
    leaveDays.mockReset().mockReturnValue(of({ days: 2, dates: [] }));
    requestLeave.mockReset().mockReturnValue(of(request('r9')));
    open.mockReset();
    notify.info.mockReset();
  });

  it('shows what is left of each paid type this year, and their requests with where each stands', async () => {
    const { el } = await render();

    expect([...el.querySelectorAll('.balance')].map((b) => [...b.children].map(text))).toEqual([
      ['Annual 2026', '7.5 of 10.5 days left', '5 asked for, waiting'],
      ['Sick 2026', '7 of 7 days left'],
    ]);
    expect([...el.querySelectorAll('tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text))).toEqual([
      ['Sun 1 Nov – Thu 5 Nov', 'Annual', '5', 'Pendingقيد الانتظار'],
      ['Sun 11 Oct – Thu 15 Oct', 'Annual', '3', 'Cut shortقُطعتfrom Wed 14 Oct · Recalled'],
      ['Mon 7 Sept', 'Annual', '1', 'RejectedمرفوضةBusy week'],
    ]);
  });

  it('asks for their own leave, of any type, and reads them again', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of(request('r9')) });
    await page['ask']();

    expect(open.mock.lastCall![0]).toBe(LeaveRequestDialog);
    const data = open.mock.lastCall![1].data;
    expect(data.employees).toBeUndefined();
    expect(data.types).toEqual(theirs.types);
    data.countDays(null, '2026-11-01', '2026-11-02');
    expect(leaveDays).toHaveBeenCalledWith('2026-11-01', '2026-11-02');
    let shown: unknown;
    data.balances(null, 2026).subscribe((b: unknown) => (shown = b));
    expect(shown).toBe(theirs.balances);
    data.balances(null, 2027).subscribe((b: unknown) => (shown = b));
    expect(shown).toEqual([]);
    data.submit({ employeeId: null, leaveTypeId: 'unpaid', startDate: '2026-11-01', endDate: '2026-11-02', reason: 'Travel' });
    expect(requestLeave).toHaveBeenCalledWith({ leaveTypeId: 'unpaid', startDate: '2026-11-01', endDate: '2026-11-02', reason: 'Travel' });
    expect(notify.info).toHaveBeenCalledWith('Asked for 5 days of Annual: your manager decides.');
    expect(leaves).toHaveBeenCalledTimes(2);
  });

  it('says when they have not asked for leave yet', async () => {
    leaves.mockReturnValue(of({ ...theirs, requests: [] }));
    const { el } = await render();
    expect(text(el.querySelector('p.muted')!)).toBe('You have not asked for leave yet.');
  });
});
