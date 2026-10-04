import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AddOrderItemRequest,
  AttachCustomerResult,
  CustomerLookupResult,
  OpenOrderRequest,
  Order,
  PaymentMethod,
} from '../api/models';

// The restaurant machine's order endpoints, one method each. Orders are addressed by publicId.
// Whether this user may do this to this order is the API's decision (OrderAccessPolicy); a
// refusal comes back as a 403 with the reason to show.
@Injectable({ providedIn: 'root' })
export class OrdersApi {
  private readonly http = inject(HttpClient);

  open(): Observable<Order[]> {
    return this.http.get<Order[]>('/api/orders/open');
  }

  recent(hours = 24): Observable<Order[]> {
    return this.http.get<Order[]>('/api/orders/recent', { params: { hours } });
  }

  get(orderId: string): Observable<Order> {
    return this.http.get<Order>(`/api/orders/${orderId}`);
  }

  create(request: OpenOrderRequest): Observable<Order> {
    return this.http.post<Order>('/api/orders', request);
  }

  addItem(orderId: string, request: AddOrderItemRequest): Observable<Order> {
    return this.http.post<Order>(`/api/orders/${orderId}/items`, request);
  }

  sendToKitchen(orderId: string): Observable<Order> {
    return this.http.post<Order>(`/api/orders/${orderId}/send-to-kitchen`, null);
  }

  attachCustomer(orderId: string, phoneNumber: string, name: string | null): Observable<AttachCustomerResult> {
    return this.http.put<AttachCustomerResult>(`/api/orders/${orderId}/customer`, { phoneNumber, name });
  }

  applyPoints(orderId: string, points: number): Observable<Order> {
    return this.http.put<Order>(`/api/orders/${orderId}/loyalty-points`, { points });
  }

  removePoints(orderId: string): Observable<Order> {
    return this.http.delete<Order>(`/api/orders/${orderId}/loyalty-points`);
  }

  checkout(orderId: string, paymentMethod: PaymentMethod): Observable<Order> {
    return this.http.post<Order>(`/api/orders/${orderId}/checkout`, { paymentMethod });
  }

  printReceipt(orderId: string): Observable<void> {
    return this.http.post<void>(`/api/orders/${orderId}/receipt`, null);
  }

  voidItem(orderId: string, orderItemId: string, quantity: number, reason: string | null): Observable<Order> {
    return this.http.post<Order>(`/api/orders/${orderId}/items/${orderItemId}/void`, { quantity, reason });
  }

  voidOrder(orderId: string, reason: string | null): Observable<Order> {
    return this.http.post<Order>(`/api/orders/${orderId}/void`, { reason });
  }
}

@Injectable({ providedIn: 'root' })
export class CustomersApi {
  private readonly http = inject(HttpClient);

  // Asks the delivery system through the till server. Status says Unavailable, rather than
  // failing, when the delivery system cannot be reached.
  findByPhone(phoneNumber: string): Observable<CustomerLookupResult> {
    return this.http.get<CustomerLookupResult>(`/api/customers/by-phone/${encodeURIComponent(phoneNumber.trim())}`);
  }
}
