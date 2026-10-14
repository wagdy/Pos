import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { SignInPage } from './sign-in-page';

describe('signing in from a keyboard', () => {
  const signIn = vi.fn();

  async function render() {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { signInOptions: () => of([{ id: 'omar', fullName: 'Omar Manager' }]), signIn, can: () => true } },
      ],
    });
    const fixture = TestBed.createComponent(SignInPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.componentInstance as unknown as Record<string, any>;
  }

  const key = (key: string) => document.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));

  beforeEach(() => signIn.mockReset().mockResolvedValue(undefined));

  it('takes a PIN typed on an Arabic keyboard as the Western digits', async () => {
    const page = await render();
    page['choose']({ id: 'omar', fullName: 'Omar Manager' });

    for (const digit of ['١', '٣', '٥', '٧']) key(digit);
    expect(page['pin']()).toBe('1357');
    key('Backspace');
    key('7');
    key('Enter');

    expect(signIn).toHaveBeenCalledWith('omar', '1357');
  });
});
