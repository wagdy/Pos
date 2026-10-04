import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';
import { AuthService } from './auth.service';
import { Permission } from './permissions';

// Shows its element only when the signed-in user has the permission (or any of them, given a
// list), and takes it away again the moment they do not:
//
//   <button *appCan="Permissions.OrderCheckout" ...>Checkout</button>
//
// The permissions are the ones GET /api/auth/me returned, so this hides exactly what the API
// would refuse. A captain therefore never sees checkout, receipts, points or voids.
@Directive({ selector: '[appCan]' })
export class CanDirective {
  private readonly auth = inject(AuthService);
  private readonly template = inject(TemplateRef<unknown>);
  private readonly container = inject(ViewContainerRef);

  readonly appCan = input.required<Permission | readonly Permission[]>();

  private shown = false;

  constructor() {
    effect(() => {
      const wanted = this.appCan();
      const allowed = typeof wanted === 'string' ? this.auth.can(wanted) : this.auth.canAny(wanted);

      if (allowed && !this.shown) {
        this.container.createEmbeddedView(this.template);
        this.shown = true;
      } else if (!allowed && this.shown) {
        this.container.clear();
        this.shown = false;
      }
    });
  }
}
