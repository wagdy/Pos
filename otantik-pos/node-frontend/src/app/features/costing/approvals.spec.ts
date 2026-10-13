import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { DeleteRequestDialog, DeleteRequestDialogData, Approvals } from './approvals';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('deleting from the costing screens', () => {
  const requestDeletion = vi.fn();
  const approvalRequests = vi.fn();
  const open = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  function service(manager: boolean): Approvals {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { requestDeletion, approvalRequests } },
        { provide: MatDialog, useValue: { open } },
        { provide: NotifyService, useValue: notify },
        { provide: AuthService, useValue: { can: (p: string) => p !== Permissions.ApprovalDecide || manager } },
      ],
    });
    return TestBed.inject(Approvals);
  }

  beforeEach(() => {
    requestDeletion.mockReset();
    approvalRequests.mockReset().mockReturnValue(of({ pending: [], decided: [] }));
    open.mockReset().mockReturnValue({ afterClosed: () => of({ reason: 'Counted twice' }) });
    notify.info.mockReset();
    notify.error.mockReset();
  });

  it("is a request for an accountant, said so, and the record is marked waiting", async () => {
    const approvals = service(false);
    const pending = { id: 'q', module: 'StockCount', action: 'Delete', recordId: 'c1', requestedBy: 'Mona' };
    requestDeletion.mockReturnValue(of({ outcome: 'Requested', request: pending }));
    approvalRequests.mockReturnValue(of({ pending: [pending], decided: [] }));

    expect(await approvals.delete('StockCount', 'c1', 'the count of 8 Oct')).toBe('Requested');

    expect(open.mock.lastCall![1].data).toEqual({ module: 'StockCount', what: 'the count of 8 Oct', canApprove: false });
    expect(requestDeletion).toHaveBeenCalledWith(expect.any(String), 'StockCount', 'c1', 'Counted twice');
    expect(notify.info).toHaveBeenCalledWith('Deletion request submitted to the manager for approval.');
    expect(approvals.pendingFor('c1')).toEqual(pending);
    expect(approvals.pendingFor('c2')).toBeUndefined();
  });

  it('is done at once for a manager: a posted record voided', async () => {
    const approvals = service(true);
    requestDeletion.mockReturnValue(of({ outcome: 'Applied', request: {} }));

    expect(await approvals.delete('PurchaseInvoice', 'i1', 'invoice F-5')).toBe('Applied');
    expect(notify.info).toHaveBeenCalledWith('Invoice F-5 voided.');
  });

  it('does nothing when cancelled, and says why when refused', async () => {
    const approvals = service(false);
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    expect(await approvals.delete('RawMaterial', 'm1', 'Salt')).toBeNull();
    expect(requestDeletion).not.toHaveBeenCalled();

    open.mockReturnValue({ afterClosed: () => of({ reason: null }) });
    requestDeletion.mockReturnValue(throwError(() => new Error('Salt is in 2 recipes.')));
    expect(await approvals.delete('RawMaterial', 'm1', 'Salt')).toBeNull();
    expect(notify.error).toHaveBeenCalled();
  });

  it("tells a manager what deleting does, and an accountant that a manager decides", async () => {
    function dialog(data: DeleteRequestDialogData): HTMLElement {
      TestBed.resetTestingModule();
      TestBed.configureTestingModule({ providers: [{ provide: MAT_DIALOG_DATA, useValue: data }] });
      const fixture = TestBed.createComponent(DeleteRequestDialog);
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    const manager = dialog({ module: 'Waste', what: 'the waste of 8 Oct', canApprove: true });
    expect(text(manager)).toContain('Void the waste of 8 Oct?');
    expect(text(manager)).toContain('What it took from stock goes back in, dated today.');

    const accountant = dialog({ module: 'Recipe', what: 'Pasta', canApprove: false, detail: 'Sales of Pasta stop taking stock until another recipe is linked to it.' });
    expect(text(accountant)).toContain('Ask to delete Pasta?');
    expect(text(accountant)).toContain('A manager approves deletions.');
    expect(text(accountant)).toContain('Sales of Pasta stop taking stock');
    expect([...accountant.querySelectorAll('button')].map(text)).toContain('Ask the manager');
  });

  it('says a save went to a manager, and lists the new ones waiting to be made', async () => {
    const approvals = service(false);
    const batch = { id: 'q2', module: 'ProductionOrder', action: 'Create', recordId: 'b1', requestedBy: 'Mona' };
    const change = { id: 'q3', module: 'PrepItem', action: 'Update', recordId: 'd1', requestedBy: 'Mona' };
    approvalRequests.mockReturnValue(of({ pending: [batch, change], decided: [] }));

    await approvals.requested({ outcome: 'Requested', request: change as never });

    expect(notify.info).toHaveBeenCalledWith('Change submitted to the manager for approval. It stays as it was until then.');
    expect(approvals.waitingToBeMade('ProductionOrder')).toEqual([batch]);
    expect(approvals.waitingToBeMade('PrepItem')).toEqual([]);
    expect(approvals.pendingFor('d1')).toEqual(change);
  });
});
