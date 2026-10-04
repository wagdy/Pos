import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Order, OrderType } from '../../core/api/models';
import { CanDirective } from '../../core/auth/can.directive';
import { Permissions } from '../../core/auth/permissions';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { liveItems, orderLabel, requiresCustomerPhone, unsentItems } from '../../core/orders/order-rules';
import { OrdersStore } from '../../core/orders/orders.store';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { CustomerDetails, CustomerDialog, CustomerDialogData } from './customer-dialog';
import { NewOrderChoice, NewOrderDialog } from './new-order-dialog';

type Filter = 'all' | OrderType;

// The floor: every open order, live. Changes made anywhere, including a captain's order sent
// from the delivery app, arrive over the till hub and land in this table as they happen, with
// anything new picked out until a cashier opens it.
@Component({
  selector: 'app-open-orders-page',
  imports: [
    CanDirective,
    MatButtonModule,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    MoneyPipe,
  ],
  templateUrl: './open-orders-page.html',
  styleUrl: './open-orders-page.scss',
})
export class OpenOrdersPage {
  protected readonly store = inject(OrdersStore);
  protected readonly connectivity = inject(ConnectivityService);
  private readonly dialog = inject(MatDialog);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  protected readonly Permissions = Permissions;
  protected readonly columns = ['order', 'items', 'kitchen', 'total', 'status', 'age'];
  protected readonly filter = signal<Filter>('all');
  protected readonly creating = signal(false);

  protected readonly rows = computed(() => {
    const filter = this.filter();
    return this.store.open().filter((order) => filter === 'all' || order.type === filter);
  });

  protected readonly counts = computed(() => {
    const open = this.store.open();
    return {
      all: open.length,
      DineIn: open.filter((o) => o.type === 'DineIn').length,
      Takeaway: open.filter((o) => o.type === 'Takeaway').length,
      Delivery: open.filter((o) => o.type === 'Delivery').length,
    };
  });

  // Ticks so the ages stay true without reloading anything.
  protected readonly now = signal(Date.now());

  protected readonly label = orderLabel;

  constructor() {
    const timer = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected liveCount(order: Order): number {
    return liveItems(order).reduce((sum, item) => sum + item.quantity, 0);
  }

  protected unsentCount(order: Order): number {
    return unsentItems(order).length;
  }

  protected age(order: Order): string {
    const minutes = Math.max(0, Math.floor((this.now() - Date.parse(order.createdAt)) / 60_000));
    if (minutes < 1) {
      return 'just now';
    }
    if (minutes < 60) {
      return `${minutes} min`;
    }
    return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
  }

  protected typeIcon(order: Order): string {
    switch (order.type) {
      case 'DineIn':
        return 'table_restaurant';
      case 'Takeaway':
        return 'takeout_dining';
      case 'Delivery':
        return 'delivery_dining';
    }
  }

  protected open(order: Order): void {
    this.store.acknowledge(order.publicId);
    void this.router.navigate(['/orders', order.publicId]);
  }

  // Kind of order, then for takeaway and delivery the customer's mobile number, which cannot be
  // skipped: the dialog has no close but Cancel.
  protected async newOrder(): Promise<void> {
    const choice = await firstValueFrom(
      this.dialog.open<NewOrderDialog, void, NewOrderChoice>(NewOrderDialog, { width: '560px' }).afterClosed(),
    );
    if (!choice) {
      return;
    }

    let customer: CustomerDetails | undefined;
    if (requiresCustomerPhone(choice.type)) {
      customer = await firstValueFrom(
        this.dialog
          .open<CustomerDialog, CustomerDialogData, CustomerDetails>(CustomerDialog, {
            data: { type: choice.type },
            disableClose: true,
            width: '560px',
          })
          .afterClosed(),
      );
      if (!customer) {
        return;
      }
    }

    this.creating.set(true);
    try {
      const order = await this.store.create({
        type: choice.type,
        tableNumber: choice.tableNumber ?? null,
        customerPhone: customer?.customerPhone ?? null,
        customerName: customer?.customerName ?? null,
        deliveryAddress: customer?.deliveryAddress ?? null,
        deliveryFee: customer?.deliveryFee ?? 0,
      });
      await this.router.navigate(['/orders', order.publicId]);
    } catch (error) {
      this.notify.error(error, 'The order could not be opened.');
    } finally {
      this.creating.set(false);
    }
  }
}
