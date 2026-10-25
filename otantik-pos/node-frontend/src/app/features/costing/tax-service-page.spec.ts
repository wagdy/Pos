import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { CostingApi } from '../../core/costing/costing.api';
import { NotifyService } from '../../core/ui/notify.service';
import { TaxServicePage } from './tax-service-page';

const text = (el: Element) => el.textContent!.replace(/\s+/g, ' ').trim();

describe('tax and service', () => {
  const financialSettings = vi.fn();
  const saveFinancialSettings = vi.fn();
  const notify = { info: vi.fn(), error: vi.fn() };

  async function render(manager: boolean) {
    TestBed.configureTestingModule({
      providers: [
        { provide: CostingApi, useValue: { financialSettings, saveFinancialSettings } },
        { provide: AuthService, useValue: { employeeActing: () => false, me: () => null, can: (p: string) => p !== Permissions.ApprovalDecide || manager } },
        { provide: NotifyService, useValue: notify },
      ],
    });
    const fixture = TestBed.createComponent(TaxServicePage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, page: fixture.componentInstance as unknown as Record<string, any> };
  }

  beforeEach(() => {
    financialSettings.mockReset().mockReturnValue(of({ taxPercent: 14, servicePercent: 0, updatedBy: null, updatedAtUtc: null }));
    saveFinancialSettings.mockReset().mockReturnValue(of({ taxPercent: 14, servicePercent: 12, updatedBy: 'Omar', updatedAtUtc: '2026-10-13T09:00:00Z' }));
    notify.info.mockReset();
  });

  it('shows the bills’ tax rate, and lets a manager set the service charge', async () => {
    const { el, page, fixture } = await render(true);

    expect(text(el.querySelector('.tile.key strong')!)).toBe('14%');
    page['service'] = 100;
    expect(page['problem']()).toBe('The service charge is a percentage from 0 up to 100.');
    page['service'] = 12;
    await page['save']();
    fixture.detectChanges();

    expect(saveFinancialSettings).toHaveBeenCalledWith(12);
    expect(notify.info).toHaveBeenCalledWith('Service charge set to 12%.');
    expect(text(el)).toContain('Last set by Omar');
  });

  it('shows the accountant the settings, for a manager to change', async () => {
    financialSettings.mockReturnValue(of({ taxPercent: null, servicePercent: 12, updatedBy: 'Omar', updatedAtUtc: '2026-10-13T09:00:00Z' }));
    const { el } = await render(false);

    expect(text(el.querySelector('.tile.key')!)).toContain('Not synced yet');
    expect([...el.querySelectorAll('button')].map(text)).not.toContain('Save');
    expect(text(el)).toContain('A manager sets the service charge.');
  });
});
