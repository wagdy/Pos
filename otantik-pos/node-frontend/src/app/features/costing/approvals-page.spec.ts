import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { CostingApi, ApprovalRequest } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { ApprovalsPage } from './approvals-page';
import { Approvals } from './approvals';

function asked(overrides: Partial<ApprovalRequest>): ApprovalRequest {
  return {
    id: 'q', module: 'Recipe', action: 'Delete', recordId: 'r', summary: 'Recipe House sauce', changes: [], reason: null, requestedBy: 'Mona',
    requestedAtUtc: '2026-10-08T09:00:00Z', status: 'Pending', decidedBy: null, decidedAtUtc: null, decisionNote: null, ...overrides,
  };
}
const invoice = asked({ id: 'q1', module: 'PurchaseInvoice', recordId: 'i1', summary: 'Invoice F-5 · 8 Oct 2026 · 1 line · L.E 150.00', reason: 'Entered twice' });
const salt = asked({ id: 'q2', module: 'RawMaterial', recordId: 'm1', summary: 'Raw material Salt' });
const earlier = asked({ id: 'q0', module: 'Waste', summary: 'Waste of 7 Oct 2026 by Mona', status: 'Rejected', decidedBy: 'Omar',
  decidedAtUtc: '2026-10-08T08:00:00Z', decisionNote: 'It was spoiled' });

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('the pending approvals', () => {
  const approvalRequests = vi.fn();
  const approveRequest = vi.fn();
  const rejectRequest = vi.fn();
  const approvalPreview = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { approvalRequests, approveRequest, rejectRequest, approvalPreview } },
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(ApprovalsPage);
    await fixture.whenStable();
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const refresh = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const button = (within: Element, label: string) => [...within.querySelectorAll('button')].find((b) => text(b).includes(label))!;
    return { el, refresh, button };
  }

  beforeEach(() => {
    approvalRequests.mockReset().mockReturnValue(of({ pending: [invoice, salt], decided: [earlier] }));
    approveRequest.mockReset().mockReturnValue(of({ ...invoice, status: 'Approved' }));
    rejectRequest.mockReset().mockReturnValue(of({ ...salt, status: 'Rejected' }));
    notify.info.mockReset();
    approvalPreview.mockReset();
  });

  it('lists what waits, with the module, the record, who asked and why, and the decisions before', async () => {
    const { el } = await render();

    expect(approvalRequests).toHaveBeenCalledWith(30);
    const rows = [...el.querySelectorAll('table')[0].querySelectorAll('tbody tr')];
    expect(rows.map((r) => [...r.querySelectorAll('td')].slice(0, 4).map(text))).toEqual([
      ['Purchasesالمشتريات', 'Deleteحذف', 'Invoice F-5 · 8 Oct 2026 · 1 line · L.E 150.00 Reason: Entered twice', 'Mona'],
      ['Raw materialالخامات', 'Deleteحذف', 'Raw material Salt', 'Mona'],
    ]);
    expect(text(el.querySelectorAll('table')[1])).toContain('Rejected by Omar');
    expect(text(el.querySelectorAll('table')[1])).toContain('It was spoiled');
    // The tab's count follows.
    expect(TestBed.inject(Approvals).pending()).toEqual([invoice, salt]);
  });

  it('approves after asking in the row: an invoice is voided', async () => {
    const { el, refresh, button } = await render();
    const first = el.querySelectorAll('tbody tr')[0];

    button(first, 'Approve').click();
    await refresh();
    const confirm = el.querySelector('tr.confirm-row')!;
    expect(text(confirm)).toContain('Void it? It is kept, marked voided: its goods go back out of stock at its own prices and its cash payment goes back');
    expect(approveRequest).not.toHaveBeenCalled();

    button(confirm, 'Approve and void').click();
    await refresh();
    expect(approveRequest).toHaveBeenCalledWith('q1');
    expect(notify.info).toHaveBeenCalledWith('Approved: it is voided.');
    expect(approvalRequests).toHaveBeenCalledTimes(2);
  });

  it('deletes a material, and shows a voided record as voided in the decisions', async () => {
    approvalRequests.mockReturnValue(of({ pending: [salt], decided: [asked({ ...invoice, id: 'q9', status: 'Approved', decidedBy: 'Omar', decidedAtUtc: '2026-10-08T10:00:00Z' })] }));
    const { el, refresh, button } = await render();

    button(el.querySelectorAll('tbody tr')[0], 'Approve').click();
    await refresh();
    const confirm = el.querySelector('tr.confirm-row')!;
    expect(text(confirm)).toContain('Delete it? It leaves the lists');
    button(confirm, 'Approve and delete').click();
    await refresh();

    expect(approveRequest).toHaveBeenCalledWith('q2');
    expect(notify.info).toHaveBeenCalledWith('Approved: it is deleted.');
    expect(text(el.querySelectorAll('table')[1])).toContain('Voided by Omar');
  });

  it('rejects with a note for the one who asked', async () => {
    const { el, refresh, button } = await render();

    button(el.querySelectorAll('tbody tr')[1], 'Reject').click();
    await refresh();
    const input = el.querySelector<HTMLInputElement>('tr.confirm-row input')!;
    expect(document.activeElement).toBe(input);
    input.value = 'Still in the recipes';
    input.dispatchEvent(new Event('input'));
    button(el.querySelector('tr.confirm-row')!, 'Reject').click();
    await refresh();

    expect(rejectRequest).toHaveBeenCalledWith('q2', 'Still in the recipes');
    expect(notify.info).toHaveBeenCalledWith('Rejected: it stays as it is, and Mona can change it again.');
  });

  it("shows what a batch asks for, and what approving it moves now, before it is approved", async () => {
    const batch = asked({
      id: 'q4', module: 'ProductionOrder', action: 'Update', recordId: 'b1', summary: 'Batch of Pizza dough made 8 Oct 2026 by Omar · 9.6 kg · L.E 120.00',
      changes: [{ label: 'Quantity to produce', before: '10 kg', after: '12 kg' }], reason: 'Weighed again',
    });
    approvalRequests.mockReturnValue(of({ pending: [batch], decided: [] }));
    approvalPreview.mockReturnValue(of({
      batch: {
        prepItemId: 'dough', prepItem: 'Pizza dough', unit: 'Gram', displayUnit: 'kg', displayUnitSize: 1000, plannedQuantity: 12_000,
        producedQuantity: 11_500, totalCost: 144, unitCostPerDisplayUnit: 12.52, missingPrices: [], shortages: [],
        lines: [{ rawMaterialId: 'flour', code: null, material: 'Flour', isPrepItem: false, unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000,
          quantity: 7_200, onHand: 20_000, short: null, costPerPurchaseUnit: 20, cost: 144 }],
      },
      replaces: {
        id: 'b1', prepItemId: 'dough', prepItem: 'Pizza dough', unit: 'Gram', displayUnit: 'kg', displayUnitSize: 1000, plannedQuantity: 10_000,
        producedQuantity: 9_600, yieldPercent: 96, totalCost: 120, unitCostPerDisplayUnit: 12.5, producedAtUtc: '2026-10-08T08:00:00Z',
        producedBy: 'Omar', notes: null, status: 'Made', voidedAtUtc: null, voidedBy: null, revisionOf: null, replacedBy: null, approvedBy: null,
        lines: [{ rawMaterialId: 'flour', code: null, material: 'Flour', isPrepItem: false, unit: 'Gram', purchaseUnit: 'kg', purchaseUnitSize: 1000,
          quantity: 6_000, onHand: null, short: null, costPerPurchaseUnit: 20, cost: 120 }],
      },
    }));
    approveRequest.mockReturnValue(of({ ...batch, status: 'Approved' }));
    const { el, refresh, button } = await render();
    const row = el.querySelector('tbody tr')!;
    expect(text(row)).toContain('Changeتعديل');
    expect(text(row)).toContain('Quantity to produce: 10 kg → 12 kg');

    button(row, 'Approve').click();
    await refresh();
    const confirm = el.querySelector('tr.confirm-row')!;
    expect(approvalPreview).toHaveBeenCalledWith('q4');
    // The net movement: 1.2 kg more flour out, 1.9 kg more dough in.
    const net = [...confirm.querySelectorAll('.moves tbody tr')].map((r) => [...r.querySelectorAll('td')].map(text));
    expect(net).toEqual([
      ['Flour', '6,000 g', '7,200 g', '-1,200 g'],
      ['Pizza dough (made)', '9,600 g', '11,500 g', '+1,900 g'],
    ]);
    expect(text(confirm)).toContain('The batch is reversed and made again with the new quantities');
    expect(confirm.classList).not.toContain('deleting');

    button(confirm, 'Approve the correction').click();
    await refresh();
    expect(approveRequest).toHaveBeenCalledWith('q4');
    expect(notify.info).toHaveBeenCalledWith('Approved: it is corrected.');
  });

  it("will not approve a batch that cannot be costed now, and turns down a new prep item with nothing made", async () => {
    const batch = asked({ id: 'q5', module: 'ProductionOrder', action: 'Create', recordId: 'b2', summary: 'New batch of Pizza dough' });
    const item = asked({ id: 'q6', module: 'PrepItem', action: 'Create', recordId: 'd2', summary: 'New prep item Garlic oil' });
    approvalRequests.mockReturnValue(of({ pending: [batch, item], decided: [] }));
    approvalPreview.mockReturnValue(of({
      batch: {
        prepItemId: 'dough', prepItem: 'Pizza dough', unit: 'Gram', displayUnit: 'kg', displayUnitSize: 1000, plannedQuantity: 10_000,
        producedQuantity: 10_000, totalCost: null, unitCostPerDisplayUnit: null, missingPrices: ['Salt'], shortages: [], lines: [],
      },
      replaces: null,
    }));
    rejectRequest.mockReturnValue(of({ ...item, status: 'Rejected' }));
    const { el, refresh, button } = await render();

    button(el.querySelectorAll('tbody tr')[0], 'Approve').click();
    await refresh();
    expect(text(el.querySelector('tr.confirm-row')!)).toContain('Salt has no cost now');
    expect(button(el.querySelector('tr.confirm-row')!, 'Approve and make').disabled).toBe(true);

    button(el.querySelector('tr.confirm-row')!, 'Cancel').click();
    await refresh();
    button(el.querySelectorAll('tbody tr')[1], 'Reject').click();
    await refresh();
    expect(text(el.querySelector('tr.confirm-row')!)).toContain('Reject: nothing is made.');
    button(el.querySelector('tr.confirm-row')!, 'Reject').click();
    await refresh();
    expect(rejectRequest).toHaveBeenCalledWith('q6', null);
    expect(notify.info).toHaveBeenCalledWith('Rejected: nothing is made.');
  });
});
