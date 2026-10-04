import { Injectable, inject } from '@angular/core';
import { HttpError, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { DeliverySystemLinkStatus, Order } from '../api/models';
import { AuthService } from '../auth/auth.service';
import { ConnectivityService } from '../connectivity/connectivity.service';
import { MenuService } from '../menu/menu.service';
import { OrdersStore } from '../orders/orders.store';

// The till's live connection to the restaurant machine's hub, /hubs/till.
//
// OrderChanged brings every change to an order this user may see, made anywhere: at another
// till, or by a captain in the delivery app, whose order the restaurant machine receives from
// the cloud and passes straight on. DeliverySystemLinkChanged says when the restaurant machine
// loses or regains the cloud.
//
// Pushes sent while this device was disconnected are not replayed, so every (re)connect reloads
// the open orders, the cloud status and the menu.
@Injectable({ providedIn: 'root' })
export class TillHubService {
  private readonly auth = inject(AuthService);
  private readonly orders = inject(OrdersStore);
  private readonly menu = inject(MenuService);
  private readonly connectivity = inject(ConnectivityService);

  private connection: HubConnection | null = null;
  private restartTimer: ReturnType<typeof setTimeout> | null = null;
  private attempt = 0;

  // Whether the till wants a connection: between start() and stop(). A connection closing
  // outside that window is not restarted.
  private wanted = false;

  async start(): Promise<void> {
    this.wanted = true;
    if (this.connection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      // A browser cannot set headers on a WebSocket, so the token goes in the query string,
      // which the API accepts on /hubs only.
      .withUrl('/hubs/till', { accessTokenFactory: () => this.auth.token() ?? '' })
      // The default gives up after four tries. A till keeps trying for as long as it takes.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) => Math.min(30_000, 1_000 * 2 ** Math.min(context.previousRetryCount, 5)),
      })
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('OrderChanged', (order: Order) => this.orders.receive(order, 'push'));
    connection.on('DeliverySystemLinkChanged', (status: DeliverySystemLinkStatus) => this.connectivity.setCloud(status));

    connection.onreconnecting(() => this.connectivity.hub.set('reconnecting'));
    connection.onreconnected(() => {
      this.connectivity.hub.set('connected');
      void this.resync();
    });
    // Only after automatic reconnection gave up (it never does) or the API closed the
    // connection, as it does when the token expires.
    connection.onclose(() => {
      this.connectivity.hub.set('disconnected');
      this.connection = null;
      this.scheduleRestart();
    });

    this.connection = connection;
    await this.connect();
  }

  async stop(): Promise<void> {
    this.wanted = false;
    if (this.restartTimer) {
      clearTimeout(this.restartTimer);
      this.restartTimer = null;
    }
    const connection = this.connection;
    this.connection = null;
    await connection?.stop();
    this.connectivity.hub.set('disconnected');
  }

  // The first connection can fail too (the till server restarting); automatic reconnection only
  // covers one that was up, so this retries it.
  private async connect(): Promise<void> {
    const connection = this.connection;
    if (!connection || connection.state !== HubConnectionState.Disconnected) {
      return;
    }
    this.connectivity.hub.set('connecting');
    try {
      await connection.start();
      this.attempt = 0;
      this.connectivity.hub.set('connected');
      await this.resync();
    } catch (error) {
      this.connectivity.hub.set('disconnected');
      // The API no longer accepts the token: retrying with it would never succeed.
      if (error instanceof HttpError && error.statusCode === 401) {
        this.auth.signOut('expired');
        return;
      }
      this.scheduleRestart();
    }
  }

  private scheduleRestart(): void {
    if (!this.wanted || !this.auth.isSignedIn() || this.restartTimer) {
      return;
    }
    const delay = Math.min(30_000, 1_000 * 2 ** Math.min(this.attempt++, 5));
    this.restartTimer = setTimeout(() => {
      this.restartTimer = null;
      if (this.connection) {
        void this.connect();
      } else {
        void this.start();
      }
    }, delay);
  }

  private async resync(): Promise<void> {
    await Promise.allSettled([this.orders.loadOpen(), this.connectivity.refreshStatus(), this.menu.load()]);
  }
}
