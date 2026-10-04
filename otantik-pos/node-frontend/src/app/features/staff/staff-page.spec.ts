import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';
import { StaffApi, TillStaff } from '../../core/staff/staff.api';
import { StaffPage } from './staff-page';

describe('the staff page', () => {
  const staff: TillStaff[] = [
    { id: 'staff-manager', fullName: 'Omar', role: 'Manager', hasPin: true, isLocalOnly: false },
    { id: 'staff-cashier', fullName: 'Sara', role: 'Cashier', hasPin: false, isLocalOnly: false },
  ];
  const all = vi.fn();
  const setPin = vi.fn();
  const open = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        { provide: StaffApi, useValue: { all, setPin } },
        { provide: MatDialog, useValue: { open } },
      ],
    });
    const fixture = TestBed.createComponent(StaffPage);
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, text: () => (fixture.nativeElement as HTMLElement).textContent ?? '' };
  }

  beforeEach(() => {
    all.mockReset().mockReturnValue(of(staff));
    setPin.mockReset().mockReturnValue(of(undefined));
    open.mockReset();
  });

  it('lists who still has no PIN first', async () => {
    const { fixture } = await render();

    const names = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.who strong')].map((e) => e.textContent?.trim());
    expect(names).toEqual(['Sara', 'Omar']);
  });

  it('sets the PIN typed in the dialog, and shows it as set', async () => {
    open.mockReturnValue({ afterClosed: () => of('2468') });
    const { fixture, text } = await render();

    ((fixture.nativeElement as HTMLElement).querySelector('.member button') as HTMLButtonElement).click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(setPin).toHaveBeenCalledWith('staff-cashier', '2468');
    expect(text()).not.toContain('No PIN yet');
  });

  it('changes nothing when the dialog is cancelled', async () => {
    open.mockReturnValue({ afterClosed: () => of(undefined) });
    const { fixture } = await render();

    ((fixture.nativeElement as HTMLElement).querySelector('.member button') as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(setPin).not.toHaveBeenCalled();
  });
});
