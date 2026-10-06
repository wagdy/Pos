import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { DeliverySystemLinkStatus, NodeStatus, PrinterProblem } from '../api/models';

export type HubState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

// Two links, and what each one going down means:
//
// - This device to the till server (the restaurant machine on the LAN). Everything goes
//   through it, so while it is down the till can only show what it already has.
// - The till server to the delivery system in the cloud. Only customer lookup and loyalty
//   points need that one. While it is down everything else carries on: the menu, adding
//   items, the kitchen printer, cash and card checkout, receipts.
@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  private readonly http = inject(HttpClient);

  // The live connection to the till server's hub, kept by TillHubService.
  readonly hub = signal<HubState>('disconnected');

  // Whether the last request reached the till server at all.
  private readonly httpReachable = signal(true);

  // The till server's own view of the delivery system: GET /api/status, then pushes.
  readonly cloud = signal<DeliverySystemLinkStatus | null>(null);

  // Printers holding tickets they cannot print, as the till server last found them: GET
  // /api/status, then pushes. Empty when everything has printed.
  readonly printers = signal<PrinterProblem[]>([]);

  readonly nodeOnline = computed(
    () => this.httpReachable() && (this.hub() === 'connected' || this.hub() === 'connecting'),
  );

  // What gates customer lookup and points.
  readonly cloudOnline = computed(() => this.nodeOnline() && this.cloud()?.cloudFeaturesAvailable === true);

  readonly cloudNotConfigured = computed(() => this.cloud()?.state === 'NotConfigured');

  markReachable(reachable: boolean): void {
    this.httpReachable.set(reachable);
  }

  setCloud(status: DeliverySystemLinkStatus): void {
    this.cloud.set(status);
  }

  setPrinters(printers: PrinterProblem[]): void {
    this.printers.set(printers);
  }

  async refreshStatus(): Promise<void> {
    const status = await firstValueFrom(this.http.get<NodeStatus>('/api/status'));
    this.cloud.set(status.deliverySystem);
    this.printers.set(status.printers ?? []);
  }
}
