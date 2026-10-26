import { Type, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { HrAccess } from '../../core/hr/hr-access';
import { Employee } from '../../core/hr/hr.api';
import { DisciplinaryAction, EmployeePerformance, Evaluation, PerformanceApi, canWithdraw, timeline } from '../../core/hr/performance.api';
import { NotifyService } from '../../core/ui/notify.service';
import { BusinessDay } from '../costing/financials';
import { DisciplinaryDialog, EvaluationDialog, KpiDialog, WithdrawActionDialog } from './performance-dialogs';
import { PerformanceSection } from './performance-section';
import { PerformanceTimeline } from './performance-timeline';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

function evaluation(id: string, date: string, over: Partial<Evaluation> = {}): Evaluation {
  return {
    id, employeeId: 'hassan', date, totalScore: 80, notes: null, reviewerName: 'Omar', recordedAtUtc: `${date}T12:00:00Z`,
    scores: [{ kpiId: 'k1', kpiName: 'Punctuality', score: 5 }, { kpiId: 'k2', kpiName: 'Hygiene', score: 4 }, { kpiId: 'k3', kpiName: 'Service', score: 3 }],
    ...over,
  };
}

function action(id: string, date: string, over: Partial<DisciplinaryAction> = {}): DisciplinaryAction {
  return {
    id, employeeId: 'hassan', date, violationType: 'Absence', severityLevel: 'VerbalWarning', notes: null, penaltyAmount: null, issuerName: 'Omar',
    issuedAtUtc: `${date}T13:00:00Z`, isWithdrawn: false, withdrawnBy: null, withdrawnAtUtc: null, withdrawalReason: null, penaltyPending: false, ...over,
  };
}

const record = { verbalWarnings: 1, firstWritten: 1, secondWritten: 0, penaltyDeductions: 1, terminations: 0, warnings: 2, next: 'PenaltyDeduction' as const };

const hassan: EmployeePerformance = {
  employeeId: 'hassan',
  employeeName: 'Hassan',
  evaluations: [evaluation('e1', '2026-10-01', { notes: 'Good month' })],
  actions: [
    action('a3', '2026-10-12', { severityLevel: 'PenaltyDeduction', penaltyAmount: 200, penaltyPending: true, violationType: 'Absence' }),
    action('a2', '2026-09-20', { severityLevel: 'FirstWritten', violationType: 'Lateness' }),
    action('a1', '2026-09-02', { violationType: 'Behaviour' }),
    action('a0', '2026-08-30', { severityLevel: 'PenaltyDeduction', penaltyAmount: 100, penaltyPending: false, violationType: 'Negligence' }),
    action('w1', '2026-10-05', { severityLevel: 'SecondWritten', isWithdrawn: true, withdrawnBy: 'Omar', withdrawnAtUtc: '2026-10-06T08:00:00Z', withdrawalReason: 'Wrong employee' }),
  ],
  record,
};

describe('the performance record', () => {
  it('lists evaluations and actions together, the latest first', () => {
    expect(timeline(hassan).map((e) => (e.kind === 'evaluation' ? e.evaluation.id : e.action.id))).toEqual(['a3', 'w1', 'e1', 'a2', 'a1', 'a0']);
  });

  it('withdraws an action not withdrawn already, a penalty deduction only while its month’s pay is open', () => {
    expect(hassan.actions.map(canWithdraw)).toEqual([true, true, true, false, false]);
  });

  async function timelineOf(allowWithdraw: boolean, p: EmployeePerformance = hassan) {
    TestBed.resetTestingModule();
    const fixture = TestBed.createComponent(PerformanceTimeline);
    fixture.componentRef.setInput('performance', p);
    fixture.componentRef.setInput('allowWithdraw', allowWithdraw);
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, fixture };
  }

  it('shows each level’s count and the next in line, and each entry as it stands', async () => {
    const { el, fixture } = await timelineOf(true);
    const withdrawn: DisciplinaryAction[] = [];
    fixture.componentInstance.withdraw.subscribe((a) => withdrawn.push(a));

    expect([...el.querySelectorAll('.record .count')].map(text)).toEqual([
      '1 Verbal warningإنذار شفهي', '1 First written warningإنذار كتابي أول', '0 Second written warningإنذار كتابي ثانٍ', '1 Penalty deductionخصم جزاء', '0 Terminationفصل',
    ]);
    expect(text(el.querySelector('.summary')!)).toBe('2 warnings in all · next in line: Penalty deductionخصم جزاء');
    const entries = [...el.querySelectorAll('.entry')];
    expect(text(entries[0])).toContain('L.E 200.00 · to come off the October payslip');
    expect(entries[1].classList).toContain('withdrawn');
    expect(text(entries[1])).toContain('Withdrawn by Omar, 6 Oct 2026: Wrong employee');
    expect([text(entries[2].querySelector('.score')!), ...[...entries[2].querySelectorAll('.kpi')].map(text), text(entries[2].querySelector('.notes')!)]).toEqual([
      '80%', 'Punctuality 5/5', 'Hygiene 4/5', 'Service 3/5', 'Good month',
    ]);
    expect(text(entries[5])).toContain('L.E 100.00 · taken off the August payslip');
    // Withdrawn: only those that may be.
    expect(entries.map((e) => !!e.querySelector('button.withdraw'))).toEqual([true, false, false, true, true, false]);
    (entries[0].querySelector('button.withdraw') as HTMLElement).click();
    expect(withdrawn.map((a) => a.id)).toEqual(['a3']);
  });

  it('is read-only where withdrawing is not given, and says when there is nothing', async () => {
    const { el } = await timelineOf(false);
    expect(el.querySelectorAll('button').length).toBe(0);

    const empty = await timelineOf(false, { ...hassan, evaluations: [], actions: [], record: { ...record, verbalWarnings: 0, firstWritten: 0, penaltyDeductions: 0, warnings: 0, next: 'VerbalWarning' } });
    expect(text(empty.el.querySelector('.empty')!)).toBe('No evaluations or disciplinary actions yet.');
  });
});

describe('the profile’s Performance & Disciplinary', () => {
  const api = { employee: vi.fn(), kpis: vi.fn(), recordEvaluation: vi.fn(), issueAction: vi.fn(), withdraw: vi.fn() };
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };
  const employee = { id: 'hassan', name: 'Hassan', hiredOn: '2026-01-05', isActive: true } as Employee;

  async function render(manager = true) {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: PerformanceApi, useValue: api },
        { provide: HrAccess, useValue: { manager: signal(manager) } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: BusinessDay, useValue: { today: () => Promise.resolve('2026-10-26') } },
      ],
    });
    const fixture = TestBed.createComponent(PerformanceSection);
    fixture.componentRef.setInput('employee', employee);
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
    return { el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    Object.values(api).forEach((f) => f.mockReset());
    api.employee.mockReturnValue(of(hassan));
    api.kpis.mockReturnValue(of([
      { id: 'k1', name: 'Punctuality', isActive: true, inUse: true },
      { id: 'k9', name: 'Old one', isActive: false, inUse: true },
    ]));
    api.withdraw.mockReturnValue(of({}));
    open.mockReset();
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it('gives a manager the KPIs, a new evaluation and a warning, and their record', async () => {
    const { el } = await render();
    expect(api.employee).toHaveBeenCalledWith('hassan');
    expect([...el.querySelectorAll('.head button')].map(text)).toEqual(['tune KPIs', 'add New evaluation تقييم', 'add Issue warning إنذار']);
    expect(el.querySelectorAll('app-performance-timeline .entry').length).toBe(6);
  });

  it('evaluates them on the KPIs in use', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of(true) });
    await page['evaluate']();
    expect(open.mock.lastCall![0]).toBe(EvaluationDialog);
    const data = open.mock.lastCall![1].data;
    expect([data.employeeName, data.kpis.map((k: { id: string }) => k.id), data.today, data.hiredOn]).toEqual(['Hassan', ['k1'], '2026-10-26', '2026-01-05']);
    data.submit({ date: '2026-10-26', scores: [{ kpiId: 'k1', score: 4 }], notes: null });
    expect(api.recordEvaluation).toHaveBeenCalledWith('hassan', { date: '2026-10-26', scores: [{ kpiId: 'k1', score: 4 }], notes: null });
    expect(notify.info).toHaveBeenCalledWith('Hassan’s evaluation recorded.');
    expect(api.employee).toHaveBeenCalledTimes(2);
  });

  it('issues a warning, the next level in their record to start with', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of(true) });
    await page['issue']();
    expect(open.mock.lastCall![0]).toBe(DisciplinaryDialog);
    expect(open.mock.lastCall![1].data.next).toBe('PenaltyDeduction');
    expect(notify.info).toHaveBeenCalledWith('Issued to Hassan.');
  });

  it('withdraws an action with why, its penalty off Corrections and the pay', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of('Issued by mistake') });
    await page['withdraw'](hassan.actions[0]);
    expect(open.mock.lastCall![0]).toBe(WithdrawActionDialog);
    expect(api.withdraw).toHaveBeenCalledWith('a3', 'Issued by mistake');
    expect(notify.info).toHaveBeenCalledWith('The penalty deduction is withdrawn: its penalty is off Corrections and the pay.');
  });

  it('keeps the KPIs a manager’s, in their own dialog', async () => {
    const { page } = await render();
    open.mockReturnValue({ afterClosed: () => of(true) });
    await page['manageKpis']();
    expect(open.mock.lastCall![0]).toBe(KpiDialog);
  });

  it('shows anyone but a manager the record, with nothing to issue or withdraw', async () => {
    const { el } = await render(false);
    expect(el.querySelectorAll('button').length).toBe(0);
    expect(el.querySelectorAll('.entry').length).toBe(6);
  });
});

async function openDialog<T>(component: Type<T>, data: unknown, providers: unknown[] = []) {
  const close = vi.fn();
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({
    providers: [{ provide: MAT_DIALOG_DATA, useValue: data }, { provide: MatDialogRef, useValue: { close } }, ...(providers as never[])],
  });
  const fixture = TestBed.createComponent(component);
  await fixture.whenStable();
  fixture.detectChanges();
  const refresh = async () => {
    await fixture.whenStable();
    fixture.componentRef.changeDetectorRef.markForCheck();
    fixture.detectChanges();
  };
  return { el: fixture.nativeElement as HTMLElement, dialog: fixture.componentInstance as unknown as Record<string, any>, close, refresh };
}

describe('the performance dialogs', () => {
  const submit = vi.fn();
  beforeEach(() => submit.mockReset().mockReturnValue(of({})));

  it('scores the KPIs 1 to 5, any left out, and works the total out as the till does', async () => {
    const kpis = [
      { id: 'k1', name: 'Punctuality', isActive: true, inUse: false },
      { id: 'k2', name: 'Hygiene', isActive: true, inUse: false },
      { id: 'k3', name: 'Service', isActive: true, inUse: false },
    ];
    const { el, dialog, close, refresh } = await openDialog(EvaluationDialog, { employeeName: 'Hassan', kpis, today: '2026-10-26', hiredOn: '2026-01-05', submit });

    await dialog['save']();
    expect(dialog['problem']()).toBe('Score at least one KPI.');
    dialog['score']('k1', 5);
    dialog['score']('k2', 4);
    dialog['score']('k3', 3);
    dialog['score']('k3', null);
    dialog['score']('k3', 3);
    await refresh();
    expect(text(el.querySelector('.total')!)).toBe('Total 80% · the average of 3 scores, out of 5');
    dialog['notes'] = ' Good month ';
    await dialog['save']();
    expect(submit).toHaveBeenCalledWith({
      date: '2026-10-26',
      scores: [{ kpiId: 'k1', score: 5 }, { kpiId: 'k2', score: 4 }, { kpiId: 'k3', score: 3 }],
      notes: 'Good month',
    });
    expect(close).toHaveBeenCalledWith(true);
  });

  it('issues the next level to start with, a penalty deduction with its amount to the piastre', async () => {
    const { el, dialog, close, refresh } = await openDialog(DisciplinaryDialog, { employeeName: 'Hassan', next: 'SecondWritten', today: '2026-10-26', hiredOn: '2026-01-05', submit });
    expect(dialog['level']()).toBe('SecondWritten');

    await dialog['save']();
    expect(dialog['problem']()).toBe('Say what the violation was.');
    dialog['violation'] = ' Absence ';
    dialog['level'].set('PenaltyDeduction');
    await refresh();
    expect(text(el.querySelector('.amount mat-hint')!)).toBe('Taken off their October pay, and listed on Corrections');
    await dialog['save']();
    expect(dialog['problem']()).toBe('A penalty deduction has its amount.');
    dialog['amount'] = 150.505;
    await dialog['save']();
    expect(dialog['problem']()).toBe('Give the penalty’s amount to the piastre at most: 150.50.');
    dialog['amount'] = 150.5;
    await dialog['save']();
    expect(submit).toHaveBeenCalledWith({ date: '2026-10-26', violationType: 'Absence', severityLevel: 'PenaltyDeduction', notes: null, penaltyAmount: 150.5 });
    expect(close).toHaveBeenCalledWith(true);

    // Any other level has no amount, whatever was typed.
    dialog['level'].set('Termination');
    await dialog['save']();
    expect(submit.mock.lastCall![0]).toMatchObject({ severityLevel: 'Termination', penaltyAmount: null });
  });

  it('withdraws only with why', async () => {
    const { el, dialog, close, refresh } = await openDialog(WithdrawActionDialog, hassan.actions[0]);
    expect(text(el.querySelector('h2')!)).toBe('Withdraw the penalty deduction? سحب الإنذار');
    expect(text(el.querySelector('mat-dialog-content')!)).toContain('Its penalty of L.E 200.00 comes off Corrections and the October pay.');
    dialog['save']();
    await refresh();
    expect(text(el.querySelector('.problem')!)).toBe('Say why it is withdrawn.');
    dialog['reason'] = ' Wrong employee ';
    dialog['save']();
    expect(close).toHaveBeenCalledWith('Wrong employee');
  });

  it('keeps the KPIs: added, renamed, retired once scored, deleted otherwise, brought back', async () => {
    const kpiApi = {
      kpis: vi.fn().mockReturnValue(of([
        { id: 'k1', name: 'Punctuality', isActive: true, inUse: true },
        { id: 'k2', name: 'Smile', isActive: true, inUse: false },
        { id: 'k3', name: 'Old one', isActive: false, inUse: true },
      ])),
      saveKpi: vi.fn().mockReturnValue(of({})),
      retireKpi: vi.fn().mockReturnValue(of({})),
      restoreKpi: vi.fn().mockReturnValue(of({})),
      deleteKpi: vi.fn().mockReturnValue(of(undefined)),
    };
    const { el, dialog, refresh } = await openDialog(KpiDialog, undefined, [{ provide: PerformanceApi, useValue: kpiApi }]);
    await refresh();

    expect([...el.querySelectorAll('.kpis li')].map((li) => [...li.querySelectorAll('button')].map(text).filter((t) => t !== 'edit'))).toEqual([
      ['Retire'], ['Delete'], ['Bring back'],
    ]);
    dialog['newName'] = ' Service ';
    await dialog['add']();
    expect(kpiApi.saveKpi).toHaveBeenCalledWith(expect.any(String), 'Service');
    expect(dialog['newName']).toBe('');

    dialog['startRename']({ id: 'k2', name: 'Smile' });
    dialog['editName'] = 'Courtesy';
    await dialog['rename']({ id: 'k2', name: 'Smile' });
    expect(kpiApi.saveKpi).toHaveBeenLastCalledWith('k2', 'Courtesy');
    expect(dialog['editing']()).toBeNull();
    expect(dialog['changed']).toBe(true);
  });
});
