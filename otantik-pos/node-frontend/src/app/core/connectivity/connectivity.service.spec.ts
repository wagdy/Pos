import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NodeStatus } from '../api/models';
import { ConnectivityService } from './connectivity.service';

describe('what the till knows about its links', () => {
  // A till that connects while the kitchen printer is off learns it from the status, and the
  // pushes keep it up to date from there.
  it('takes the printers holding tickets from the till server, and clears them when they print', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const connectivity = TestBed.inject(ConnectivityService);
    const http = TestBed.inject(HttpTestingController);

    const reading = connectivity.refreshStatus();
    http.expectOne('/api/status').flush({
      deliverySystem: { state: 'Online', sinceUtc: '2026-10-06T10:00:00Z', cloudFeaturesAvailable: true },
      printers: [{ printer: 'Kitchen', problem: 'Connection refused', waiting: 2, oldestWaitingSinceUtc: '2026-10-06T10:01:00Z' }],
    } satisfies NodeStatus);
    await reading;

    expect(connectivity.printers().map((p) => `${p.printer}: ${p.waiting}`)).toEqual(['Kitchen: 2']);

    connectivity.setPrinters([]);
    expect(connectivity.printers()).toEqual([]);
    http.verify();
  });
});
