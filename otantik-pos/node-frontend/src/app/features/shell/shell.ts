import { Component, OnDestroy, OnInit, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Order } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { CanDirective } from '../../core/auth/can.directive';
import { MoneyPermissions, Permissions } from '../../core/auth/permissions';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { MenuService } from '../../core/menu/menu.service';
import { orderLabel } from '../../core/orders/order-rules';
import { OrdersStore } from '../../core/orders/orders.store';
import { TillHubService } from '../../core/realtime/till-hub.service';
import { NotifyService } from '../../core/ui/notify.service';

// Everything after sign-in: the toolbar with both connections' state, the offline banners, and
// the live connection to the till server, which lives as long as this does.
@Component({
  selector: 'app-shell',
  imports: [
    CanDirective,
    MatButtonModule,
    MatIconModule,
    MatMenuModule,
    MatToolbarModule,
    MatTooltipModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
  ],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit, OnDestroy {
  protected readonly auth = inject(AuthService);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly Permissions = Permissions;
  private readonly hub = inject(TillHubService);
  private readonly orders = inject(OrdersStore);
  private readonly menu = inject(MenuService);
  private readonly notify = inject(NotifyService);
  private readonly router = inject(Router);

  // A role with none of the money rights: a captain who reached the till, say. Told plainly
  // rather than left wondering where the buttons went.
  protected readonly orderTakingOnly = computed(() => !this.auth.canAny(MoneyPermissions));

  protected readonly cloudLabel = computed(() => {
    switch (this.connectivity.cloud()?.state) {
      case 'Online':
        return 'Delivery system: online';
      case 'Offline':
        return 'Delivery system: offline';
      case 'NotConfigured':
        return 'Delivery system: not connected';
      default:
        return 'Delivery system: connecting';
    }
  });

  ngOnInit(): void {
    // Someone else opened an order: a captain from the delivery app, or another till.
    this.orders.onArrival = (order: Order) =>
      this.notify.attention(`New ${order.type === 'DineIn' ? 'dine-in' : order.type.toLowerCase()} order: ${orderLabel(order)}`);

    void this.menu.load().catch(() => undefined);
    void this.hub.start();
  }

  ngOnDestroy(): void {
    this.orders.onArrival = undefined;
    void this.hub.stop();
  }

  protected async signOut(): Promise<void> {
    await this.hub.stop();
    this.orders.clear();
    this.auth.signOut();
  }

  protected roleLabel(): string {
    switch (this.auth.me()?.role) {
      case 'CaptainOrder':
        return 'Captain';
      case 'DeliveryCaptain':
        return 'Delivery captain';
      default:
        return this.auth.me()?.role ?? '';
    }
  }

  protected openOrders(): void {
    void this.router.navigate(['/orders']);
  }
}
