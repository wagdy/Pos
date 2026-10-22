import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { Observable, firstValueFrom } from 'rxjs';
import { CostingApi } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';
import { saveBlob } from '../../core/ui/save-blob';

// An invoice's photo or scan, fetched with the manager's sign-in (a plain link would carry none)
// and shown in the page: a photo as it is, a PDF in the browser's own viewer. A till running
// full screen has no tabs to open it in. Another file (an employee's ID card) is shown the same
// way, given how to fetch it.
@Component({
  selector: 'app-paper-preview',
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule],
  template: `
    @if (error()) {
      <p class="error">{{ error() }}</p>
    } @else if (image(); as src) {
      <img [src]="src" [alt]="fileName()" />
    } @else if (document(); as src) {
      <iframe [src]="src" [title]="fileName()"></iframe>
    } @else {
      <mat-progress-bar mode="indeterminate" />
    }
    <div class="actions">
      <span class="name">{{ fileName() }}</span>
      <button mat-button (click)="download()" [disabled]="!blob()"><mat-icon>download</mat-icon> Download</button>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    /* As tall as where it is shown lets it: --paper-height, 70vh unless said. */
    img {
      display: block;
      max-width: 100%;
      max-height: var(--paper-height, 70vh);
      margin: 0 auto;
    }
    iframe {
      display: block;
      width: 100%;
      min-width: var(--paper-min-width, min(80vw, 640px));
      height: var(--paper-height, 70vh);
      border: 0;
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 4px;
    }
    .name {
      flex: 1;
      color: var(--mat-sys-on-surface-variant);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class PaperPreview {
  private readonly api = inject(CostingApi);
  private readonly sanitizer = inject(DomSanitizer);

  readonly invoiceId = input<string | null>(null);
  // Another file than an invoice's paper: how it is fetched.
  readonly fetch = input<(() => Observable<Blob>) | null>(null);
  readonly fileName = input.required<string>();

  protected readonly image = signal<string | null>(null);
  protected readonly document = signal<SafeResourceUrl | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly blob = signal<Blob | null>(null);
  private url: string | null = null;

  constructor() {
    effect(() => {
      const [id, fetch] = [this.invoiceId(), this.fetch()];
      untracked(() => void this.load(fetch ?? (() => this.api.purchaseInvoiceFile(id!))));
    });
    inject(DestroyRef).onDestroy(() => this.release());
  }

  protected download(): void {
    const blob = this.blob();
    if (blob) {
      saveBlob(blob, this.fileName());
    }
  }

  private async load(fetch: () => Observable<Blob>): Promise<void> {
    this.release();
    this.image.set(null);
    this.document.set(null);
    this.error.set(null);
    try {
      const blob = await firstValueFrom(fetch());
      this.blob.set(blob);
      this.url = URL.createObjectURL(blob);
      if (blob.type.startsWith('image/')) {
        this.image.set(this.url);
      } else {
        // A PDF, in the browser's own viewer. The URL is one made here, of a file the till checked
        // was a PDF by its first bytes when it was uploaded.
        this.document.set(this.sanitizer.bypassSecurityTrustResourceUrl(this.url));
      }
    } catch (error) {
      this.error.set(errorMessage(error, 'The file could not be loaded.'));
    }
  }

  private release(): void {
    if (this.url) {
      URL.revokeObjectURL(this.url);
      this.url = null;
    }
  }
}

export interface InvoicePaperData {
  invoiceId: string;
  fileName: string;
}

// The paper alone, over the page: from the invoice being entered, or a paperclip in the history.
@Component({
  selector: 'app-invoice-paper',
  imports: [MatButtonModule, MatDialogModule, PaperPreview],
  template: `
    <h2 mat-dialog-title>{{ data.fileName }} <span class="inline-ar">صورة الفاتورة</span></h2>
    <mat-dialog-content>
      <app-paper-preview [invoiceId]="data.invoiceId" [fileName]="data.fileName" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-flat-button mat-dialog-close>Close</button>
    </mat-dialog-actions>
  `,
  styles: `
    .inline-ar {
      direction: rtl;
      unicode-bidi: isolate;
      opacity: 0.75;
      margin-left: 6px;
    }
  `,
})
export class InvoicePaperDialog {
  protected readonly data = inject<InvoicePaperData>(MAT_DIALOG_DATA);
}

// The icon beside a paper's name: a PDF or a photo.
export function paperIcon(fileName: string): string {
  return fileName.toLowerCase().endsWith('.pdf') ? 'picture_as_pdf' : 'image';
}

export function viewInvoicePaper(dialog: MatDialog, invoiceId: string, fileName: string): void {
  dialog.open<InvoicePaperDialog, InvoicePaperData>(InvoicePaperDialog, { data: { invoiceId, fileName }, maxWidth: '95vw' });
}
