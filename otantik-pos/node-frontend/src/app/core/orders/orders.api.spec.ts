import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { Order } from '../api/models';
import { OrdersApi } from './orders.api';

describe('the orders API', () => {
  // The till refuses the payment if the bill has changed since: a captain's round can land
  // while the payment screen is open.
  it('sends the total the cashier was shown along with the payment', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);

    const paying = firstValueFrom(TestBed.inject(OrdersApi).checkout('order-1', 'Cash', 17.07, 20));
    const request = http.expectOne('/api/orders/order-1/checkout');
    expect(request.request.body).toEqual({ paymentMethod: 'Cash', expectedTotal: 17.07, cashReceived: 20 });
    request.flush({} as Order);
    await paying;

    http.verify();
  });
});
