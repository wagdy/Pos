import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { AddOrderItemRequest, AttachCustomerResult, OpenOrderRequest, Order, PaymentMethod } from '../api/models';
import { AuthService } from '../auth/auth.service';
import { awaitsPayment, isOpen } from './order-rules';
import { OrdersApi } from './orders.api';

// Every order this till knows about, kept current from two directions:
//
// - its own changes, from each endpoint's response, at once;
// - everyone else's, pushed by the till hub: another till, or a captain's order from the
//   delivery app, which appears on the open-orders screen the moment it arrives.
//
// Both land in `receive`, so the screen never has to be refreshed by hand.
@Injectable({ providedIn: 'root' })
export class OrdersStore {
  private readonly api = inject(OrdersApi);
  private readonly auth = inject(AuthService);

  private readonly byId = signal<ReadonlyMap<string, Order>>(new Map());
  private readonly _arrived = signal<ReadonlySet<string>>(new Set());
  private readonly _loaded = signal(false);
  private readonly _changes = signal(0);

  // The floor: everything still to be paid, a delivery handed over before its cash came back
  // included, oldest first.
  readonly open = computed(() =>
    [...this.byId().values()].filter(awaitsPayment).sort((a, b) => a.createdAt.localeCompare(b.createdAt)),
  );

  // Open orders that arrived from somewhere else since this till last opened them, to stand out.
  readonly arrived = this._arrived.asReadonly();
  readonly loaded = this._loaded.asReadonly();

  // Counts every change to any order, from here or pushed: a screen that shows orders some
  // other way (the day's report) watches it to know when to look again.
  readonly changes = this._changes.asReadonly();

  // Raised for an order this till had never seen, pushed by the hub: someone else just opened
  // it. The shell tells the cashier.
  onArrival?: (order: Order) => void;

  // Read inside a computed or a template, it tracks: the screen follows every change to the order.
  find(publicId: string): Order | undefined {
    return this.byId().get(publicId);
  }

  // The full list from the till server: on start, and after every reconnect, since pushes sent
  // while this device was away are not replayed.
  async loadOpen(): Promise<void> {
    const open = await firstValueFrom(this.api.open());
    this.byId.update((current) => {
      const next = new Map<string, Order>();
      // Finished orders this till has on screen stay; unpaid ones come from the server's list.
      for (const order of current.values()) {
        if (!awaitsPayment(order)) {
          next.set(order.publicId, order);
        }
      }
      for (const order of open) {
        next.set(order.publicId, order);
      }
      return next;
    });
    this._loaded.set(true);
  }

  async load(publicId: string): Promise<Order> {
    return this.track(this.api.get(publicId));
  }

  // A pushed order replaces this till's copy only if it is not older: two pushes can overtake
  // each other, and a response can arrive after a push of the same change.
  receive(order: Order, origin: 'push' | 'local'): void {
    const known = this.byId().get(order.publicId);
    // Compared as times, not strings: the API writes as many fractional digits as it has.
    if (known && Date.parse(known.updatedAt) > Date.parse(order.updatedAt)) {
      return;
    }
    this.byId.update((current) => new Map(current).set(order.publicId, order));
    this._changes.update((n) => n + 1);

    // The push for an order opened here can come back before the response does. Without this,
    // the cashier who had just opened a takeaway was told of a "new order" from someone else.
    const mine = order.createdByUserId !== null && order.createdByUserId === this.auth.me()?.id;
    if (origin === 'push' && !known && !mine && isOpen(order)) {
      this._arrived.update((ids) => new Set(ids).add(order.publicId));
      this.onArrival?.(order);
    }
  }

  // The cashier has looked at it.
  acknowledge(publicId: string): void {
    if (this._arrived().has(publicId)) {
      this._arrived.update((ids) => {
        const next = new Set(ids);
        next.delete(publicId);
        return next;
      });
    }
  }

  create(request: OpenOrderRequest): Promise<Order> {
    return this.track(this.api.create(request));
  }

  addItem(publicId: string, request: AddOrderItemRequest): Promise<Order> {
    return this.track(this.api.addItem(publicId, request));
  }

  sendToKitchen(publicId: string): Promise<Order> {
    return this.track(this.api.sendToKitchen(publicId));
  }

  async attachCustomer(publicId: string, phoneNumber: string, name: string | null): Promise<AttachCustomerResult> {
    const result = await firstValueFrom(this.api.attachCustomer(publicId, phoneNumber, name));
    this.receive(result.order, 'local');
    return result;
  }

  applyPoints(publicId: string, points: number): Promise<Order> {
    return this.track(this.api.applyPoints(publicId, points));
  }

  removePoints(publicId: string): Promise<Order> {
    return this.track(this.api.removePoints(publicId));
  }

  checkout(publicId: string, paymentMethod: PaymentMethod, expectedTotal: number, cashReceived: number | null = null): Promise<Order> {
    return this.track(this.api.checkout(publicId, paymentMethod, expectedTotal, cashReceived));
  }

  printReceipt(publicId: string): Promise<void> {
    return firstValueFrom(this.api.printReceipt(publicId));
  }

  voidItem(publicId: string, orderItemId: string, quantity: number, reason: string | null): Promise<Order> {
    return this.track(this.api.voidItem(publicId, orderItemId, quantity, reason));
  }

  voidOrder(publicId: string, reason: string | null): Promise<Order> {
    return this.track(this.api.voidOrder(publicId, reason));
  }

  clear(): void {
    this.byId.set(new Map());
    this._arrived.set(new Set());
    this._loaded.set(false);
  }

  private async track(request: Observable<Order>): Promise<Order> {
    const order = await firstValueFrom(request);
    this.receive(order, 'local');
    return order;
  }
}
