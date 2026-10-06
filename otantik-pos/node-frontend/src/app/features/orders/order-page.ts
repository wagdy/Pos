import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AddOrderItemRequest, MenuItem, Order, OrderItem, VoidType } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { CanDirective } from '../../core/auth/can.directive';
import { MoneyPermissions, Permissions } from '../../core/auth/permissions';
import { ConnectivityService } from '../../core/connectivity/connectivity.service';
import { MenuService } from '../../core/menu/menu.service';
import {
  addItemProblem,
  awaitsPayment,
  billedSubtotal,
  isOpen,
  isSentToKitchen,
  isSettled,
  isVoided,
  itemName,
  lineTotal,
  liveItems,
  orderLabel,
  paymentMethodLabel,
  statusText,
  unsentItems,
  voidPermissionFor,
  voidTypeFor,
} from '../../core/orders/order-rules';
import { OrdersStore } from '../../core/orders/orders.store';
import { MoneyPipe } from '../../core/ui/money.pipe';
import { NotifyService } from '../../core/ui/notify.service';
import { AddItemDialog, AddItemDialogData } from './add-item-dialog';
import { CheckoutDialog, CheckoutDialogData, CheckoutResult } from './checkout-dialog';
import { PointsDialog, PointsDialogData } from './points-dialog';
import { VoidDialog, VoidDialogData, VoidRequest } from './void-dialog';

// One order: the menu on one side, the bill on the other.
//
// The bill is the live order. A change made elsewhere, a captain adding a round from the delivery
// app or another till voiding a line, shows up here as it happens.
//
// Every action is shown only to a role that has its permission (from GET /api/auth/me), and
// enabled only while the order's state and the connections allow it. A captain sees adding and
// sending only: no checkout, no receipt, no points, no voids. With the delivery system offline,
// points are off (they live in the customer's account in the cloud) and everything else works.
@Component({
  selector: 'app-order-page',
  imports: [
    CanDirective,
    FormsModule,
    MatButtonModule,
    MatCardModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatTabsModule,
    MatTooltipModule,
    MoneyPipe,
    NgTemplateOutlet,
    RouterLink,
  ],
  templateUrl: './order-page.html',
  styleUrl: './order-page.scss',
})
export class OrderPage {
  private readonly store = inject(OrdersStore);
  private readonly dialog = inject(MatDialog);
  private readonly notify = inject(NotifyService);
  protected readonly auth = inject(AuthService);
  protected readonly menu = inject(MenuService);
  protected readonly connectivity = inject(ConnectivityService);

  // From the route, orders/:orderId.
  readonly orderId = input.required<string>();

  protected readonly Permissions = Permissions;
  protected readonly MoneyPermissions = MoneyPermissions;

  protected readonly order = computed(() => this.store.find(this.orderId()));
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly search = signal('');

  // Items can still be added and sent.
  protected readonly editable = computed(() => {
    const order = this.order();
    return order !== undefined && isOpen(order);
  });

  // Payment and points can still be taken: longer than editable, as a delivery can be handed
  // over before its cash comes back to the till.
  protected readonly payable = computed(() => {
    const order = this.order();
    return order !== undefined && awaitsPayment(order);
  });

  protected readonly canAddItems = computed(
    () => this.editable() && this.auth.can(Permissions.OrderAddItems) && this.connectivity.nodeOnline() && !this.busy(),
  );

  protected readonly unsent = computed(() => {
    const order = this.order();
    return order ? unsentItems(order) : [];
  });

  protected readonly live = computed(() => {
    const order = this.order();
    return order ? liveItems(order) : [];
  });

  protected readonly settled = computed(() => {
    const order = this.order();
    return order !== undefined && isSettled(order);
  });

  // Points belong to the customer's account in the delivery system: without it they can be
  // neither checked nor spent.
  protected readonly pointsBlocked = computed(() => !this.connectivity.cloudOnline());

  // A bill with points on it is paid partly from the customer's account, which needs the cloud.
  // Removing the points, which the till can do alone, frees it for checkout again.
  protected readonly checkoutBlockedByPoints = computed(
    () => (this.order()?.pointsRedeemed ?? 0) > 0 && !this.connectivity.cloudOnline(),
  );

  // What voiding the whole order would be: a cancellation before payment, a refund after it.
  protected readonly orderVoidType = computed<VoidType | null>(() => {
    const order = this.order();
    if (!order || order.status === 'Cancelled' || order.paymentStatus === 'Refunded') {
      return null;
    }
    if (isSettled(order)) {
      return 'AfterPayment';
    }
    return liveItems(order).some(isSentToKitchen) ? 'AfterKitchen' : 'BeforeKitchen';
  });

  protected readonly searchResults = computed(() => this.menu.search(this.search()));

  protected readonly label = orderLabel;
  protected readonly paymentMethodLabel = paymentMethodLabel;
  protected readonly statusText = statusText;
  protected readonly itemName = itemName;
  protected readonly lineTotal = lineTotal;
  protected readonly isVoided = isVoided;
  protected readonly isSentToKitchen = isSentToKitchen;
  protected readonly billedSubtotal = billedSubtotal;

  constructor() {
    effect(() => {
      const id = this.orderId();
      untracked(() => void this.load(id));
    });
  }

  private async load(publicId: string): Promise<void> {
    this.loadError.set(null);
    this.store.acknowledge(publicId);
    try {
      await this.store.load(publicId);
    } catch (error) {
      // Still on screen from before? Then this was only a refresh that failed.
      if (!this.store.find(publicId)) {
        this.loadError.set(this.messageFor(error));
      }
    }
  }

  protected fromPrice(item: MenuItem): number {
    const sizes = item.variants.filter((v) => v.isAvailable).map((v) => v.price);
    return sizes.length > 0 ? Math.min(...sizes) : item.price;
  }

  protected soldOut(item: MenuItem): boolean {
    return !item.isAvailable || (item.variants.length > 0 && item.variants.every((v) => !v.isAvailable));
  }

  // One tap for a plain item; sizes and add-ons need a choice first.
  protected async pick(item: MenuItem): Promise<void> {
    const order = this.order();
    if (!order || !this.canAddItems()) {
      return;
    }
    let request: AddOrderItemRequest | undefined;
    if (item.variants.length > 0 || item.menuItemAddOns.length > 0) {
      request = await firstValueFrom(
        this.dialog
          .open<AddItemDialog, AddItemDialogData, AddOrderItemRequest>(AddItemDialog, { data: { menuItem: item }, width: '520px' })
          .afterClosed(),
      );
    } else {
      const problem = addItemProblem(item, null, []);
      if (problem) {
        this.notify.info(problem);
        return;
      }
      request = { menuItemId: item.id, quantity: 1, variantId: null, addOnIds: [], notes: null };
    }
    if (request) {
      await this.run(() => this.store.addItem(order.publicId, request), `${item.name} added`);
    }
  }

  protected sendToKitchen(): Promise<void> {
    const order = this.order()!;
    const count = this.unsent().length;
    return this.run(() => this.store.sendToKitchen(order.publicId), `Sent ${count} ${count === 1 ? 'line' : 'lines'} to the kitchen`);
  }

  // The number was taken while the delivery system was unreachable: try the lookup again now.
  protected lookUpCustomerAgain(): Promise<void> {
    const order = this.order()!;
    return this.run(async () => {
      const result = await this.store.attachCustomer(order.publicId, order.customerPhone, order.customerName || null);
      this.notify.info(
        result.lookup === 'Found'
          ? `Found ${result.order.customerName}'s account`
          : result.lookup === 'NotFound'
            ? 'No account has this number: a walk-in'
            : 'The delivery system still cannot be reached',
      );
    });
  }

  protected async applyPoints(): Promise<void> {
    const order = this.order()!;
    const points = await firstValueFrom(
      this.dialog.open<PointsDialog, PointsDialogData, number>(PointsDialog, { data: { order }, width: '480px' }).afterClosed(),
    );
    if (points) {
      await this.run(() => this.store.applyPoints(order.publicId, points), `${points} points applied`);
    }
  }

  protected removePoints(): Promise<void> {
    const order = this.order()!;
    return this.run(() => this.store.removePoints(order.publicId), 'Points removed');
  }

  protected async checkout(): Promise<void> {
    const order = this.order()!;
    // The live bill, so the dialog shows a round that lands while it is open.
    const live = computed(() => this.order() ?? order);
    const result = await firstValueFrom(
      this.dialog
        .open<CheckoutDialog, CheckoutDialogData, CheckoutResult>(CheckoutDialog, { data: { order: live }, width: '480px' })
        .afterClosed(),
    );
    if (result) {
      await this.run(() => this.store.checkout(order.publicId, result.method, result.total, result.cashReceived), 'Paid');
    }
  }

  protected printReceipt(): Promise<void> {
    const order = this.order()!;
    return this.run(() => this.store.printReceipt(order.publicId), 'Receipt sent to the printer');
  }

  protected canVoidItem(item: OrderItem): boolean {
    const order = this.order();
    const type = order ? voidTypeFor(order, item) : null;
    return type !== null && this.auth.can(voidPermissionFor(type));
  }

  protected canVoidOrder(): boolean {
    const type = this.orderVoidType();
    return type !== null && this.auth.can(voidPermissionFor(type));
  }

  protected async voidItem(item: OrderItem): Promise<void> {
    const order = this.order()!;
    const type = voidTypeFor(order, item);
    if (!type) {
      return;
    }
    const request = await this.askVoid({ order, item, type });
    if (request) {
      await this.run(
        () => this.store.voidItem(order.publicId, item.publicId, request.quantity, request.reason),
        type === 'AfterPayment' ? 'Refunded' : 'Voided',
      );
    }
  }

  protected async voidOrder(): Promise<void> {
    const order = this.order()!;
    const type = this.orderVoidType();
    if (!type) {
      return;
    }
    const request = await this.askVoid({ order, item: null, type });
    if (request) {
      await this.run(
        () => this.store.voidOrder(order.publicId, request.reason),
        type === 'AfterPayment' ? 'Order refunded' : 'Order voided',
      );
    }
  }

  private askVoid(data: VoidDialogData): Promise<VoidRequest | undefined> {
    return firstValueFrom(
      this.dialog.open<VoidDialog, VoidDialogData, VoidRequest>(VoidDialog, { data, width: '480px' }).afterClosed(),
    );
  }

  // One action at a time, and every refusal shown as the API worded it.
  private async run(action: () => Promise<unknown>, done?: string): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    try {
      await action();
      if (done) {
        this.notify.info(done);
      }
    } catch (error) {
      this.notify.error(error);
    } finally {
      this.busy.set(false);
    }
  }

  private messageFor(error: unknown): string {
    return error && typeof error === 'object' && 'status' in error && error.status === 404
      ? 'There is no such order.'
      : 'The order could not be loaded.';
  }

}
