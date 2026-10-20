import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { AdvanceRow, ApprovalRequest, CostingApi } from '../../core/costing/costing.api';
import { AdvancesPage } from './advances-page';
import { Approvals } from './approvals';
import { Treasury } from './financials';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function advance(id: string, employeeId: string, employee: string, amount: number, extra: Partial<AdvanceRow> = {}): AdvanceRow {
  return {
    id, date: '2026-10-12', employeeId, employee, amount, payrollMonth: '2026-10-01', fromTakingsOf: null, notes: null, recordedBy: 'Omar', approvedBy: null,
    voidedAtUtc: null, voidedBy: null, ...extra,
  };
}

describe('employees’ advances', () => {
  const advances = vi.fn();
  const enter = vi.fn();
  const remove = vi.fn();
  const pending = signal<ApprovalRequest[]>([]);
  const approvals = {
    refresh: vi.fn(),
    delete: remove,
    pendingFor: (id: string) => pending().find((r) => r.recordId === id),
    waitingToBeMade: (module: string) => pending().filter((r) => r.module === module && r.action === 'Create'),
  };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { advances } },
        { provide: Treasury, useValue: { enter } },
        { provide: Approvals, useValue: approvals },
      ],
    });
    const fixture = TestBed.createComponent(AdvancesPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    advances.mockReset().mockReturnValue(of([
      advance('a1', 'hassan', 'Hassan', 300, { fromTakingsOf: '2026-10-12', notes: 'Rent' }),
      advance('a2', 'mona', 'Mona', 500, { payrollMonth: '2026-11-01' }),
      advance('a3', 'hassan', 'Hassan', 200),
      advance('a4', 'hassan', 'Hassan', 999, { voidedAtUtc: '2026-10-13T10:00:00Z', voidedBy: 'Omar' }),
    ]));
    enter.mockReset().mockResolvedValue(true);
    remove.mockReset().mockResolvedValue('Applied');
    pending.set([]);
  });

  it('lists the month’s advances, where each was paid from and whose pay it comes off, with each employee’s total', async () => {
    const { el } = await render();

    expect(text(el.querySelector('.toolbar .muted')!)).toBe('L.E 1,000.00 advanced');
    expect([...el.querySelectorAll('.totals .chip')].map(text)).toEqual(['Hassan L.E 500.00', 'Mona L.E 500.00']);
    const first = el.querySelectorAll('tbody tr')[0];
    expect(text(first.querySelectorAll('td')[1])).toBe('Hassan From the takings of 12 Oct · من إيراد يوم Rent');
    expect(text(first.querySelectorAll('td')[2])).toBe('October 2026');
    expect(text(el.querySelectorAll('tbody tr')[1].querySelectorAll('td')[2])).toBe('November 2026');
    expect(el.querySelectorAll('tbody tr')[3].classList).toContain('voided');
  });

  it('pays a new one through the safe entry dialog, and voids one as a safe entry is', async () => {
    const { page } = await render();

    await page['add']();
    expect(enter).toHaveBeenCalledWith('EmployeeAdvance');
    await page['voidAdvance'](advance('a1', 'hassan', 'Hassan', 300));
    expect(remove).toHaveBeenCalledWith('Treasury', 'a1', 'the advance to Hassan of 12 Oct');
  });

  it('lists one an accountant asked for as waiting: no cash has moved', async () => {
    pending.set([
      { id: 'r1', module: 'Treasury', action: 'Create', recordId: 't1', summary: 'Employee advance · Hassan · L.E 1,000.00', requestedBy: 'Sara' },
      { id: 'r2', module: 'Treasury', action: 'Create', recordId: 't2', summary: 'Supplier payment · Nile Farms · L.E 50.00', requestedBy: 'Sara' },
    ] as ApprovalRequest[]);
    const { el } = await render();

    expect([...el.querySelectorAll('.waiting li')].map(text)).toEqual([
      'Waiting for approvalبانتظار الموافقة Employee advance · Hassan · L.E 1,000.00 · asked by Sara: no cash has moved',
    ]);
  });
});
