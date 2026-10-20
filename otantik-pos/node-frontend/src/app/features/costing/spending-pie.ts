import { DecimalPipe } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';
import { MoneyPipe } from '../../core/ui/money.pipe';

// One slice of a pie: what it is and how much. A colour of its own, or the next of the palette.
export interface PieSlice {
  key: string;
  en: string;
  ar: string | null;
  amount: number;
  color?: string;
}

interface DrawnSlice extends PieSlice {
  color: string;
  percent: number;
  path: string;
  // Where its tooltip points, as percentages of the pie's box.
  tipX: number;
  tipY: number;
  // For "Other": how many it holds.
  count?: number;
}

// The reference palette's categorical slots, in order, validated together on the light surface,
// and a grey kept for "Other".
export const pieColors = ['#2a78d6', '#eb6834', '#1b998b', '#8e5bc4', '#d9a404', '#c23b6e', '#5c9e31'];
const otherColor = '#7a8794';

// Past this many, the smallest go together as "Other": a pie of slivers says nothing.
const mostSlices = 8;

const size = 200;
const centre = size / 2;
const outer = 90;
const inner = 54;

// A ring of slices, largest first from the top, the total in its middle, with a legend that has
// every slice's amount and share. Pointing at a slice or its legend row says the same in a tooltip.
@Component({
  selector: 'app-spending-pie',
  imports: [DecimalPipe, MoneyPipe],
  template: `
    <h3>{{ heading() }} <span class="inline-ar">{{ headingAr() }}</span></h3>
    @if (total() > 0) {
      <div class="pie">
        <svg [attr.viewBox]="'0 0 ' + size + ' ' + size" role="img" [attr.aria-label]="describe()">
          @for (s of drawn(); track s.key; let i = $index) {
            <path
              [attr.d]="s.path"
              [attr.fill]="s.color"
              fill-rule="evenodd"
              [class.faded]="hovered() !== null && hovered() !== i"
              (mouseenter)="hovered.set(i)"
              (mouseleave)="hovered.set(null)"
            />
          }
        </svg>
        <div class="centre" aria-hidden="true">
          <small>L.E</small>
          <strong>{{ total() | number: '1.0-0' }}</strong>
        </div>
        @if (tip(); as t) {
          <div class="tip" role="tooltip" [style.left.%]="t.tipX" [style.top.%]="t.tipY">
            <strong>{{ t.en }}</strong>
            @if (t.ar) {
              <span class="ar">{{ t.ar }}</span>
            }
            @if (t.count) {
              <span>{{ t.count }} categories</span>
            }
            <b class="amount">{{ t.amount | money }}</b>
            <span>{{ t.percent | number: '1.1-1' }}% {{ shareOf() }}</span>
          </div>
        }
      </div>
      <ul class="legend">
        @for (s of drawn(); track s.key; let i = $index) {
          <li
            tabindex="0"
            [class.active]="hovered() === i"
            (mouseenter)="hovered.set(i)"
            (mouseleave)="hovered.set(null)"
            (focus)="hovered.set(i)"
            (blur)="hovered.set(null)"
          >
            <span class="swatch" [style.background]="s.color"></span>
            <span class="name">
              {{ s.en }}
              @if (s.ar) {
                <span class="ar">{{ s.ar }}</span>
              }
            </span>
            <span class="amount">{{ s.amount | money }}</span>
            <span class="percent">{{ s.percent | number: '1.1-1' }}%</span>
          </li>
        }
      </ul>
    } @else {
      <p class="none">{{ empty() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: 12px 14px;
      border-radius: 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      background: var(--mat-sys-surface);
      break-inside: avoid;
    }
    h3 {
      margin: 0 0 10px;
      font: var(--mat-sys-title-small);
    }
    .pie {
      position: relative;
      width: 100%;
      max-width: 200px;
      margin: 0 auto 12px;
    }
    svg {
      display: block;
      width: 100%;
      height: auto;
    }
    path {
      stroke: var(--mat-sys-surface);
      stroke-width: 1.5;
      transition: opacity 120ms;
      print-color-adjust: exact;
      -webkit-print-color-adjust: exact;

      &.faded {
        opacity: 0.35;
      }
    }
    .centre {
      position: absolute;
      inset: 0;
      display: grid;
      place-content: center;
      text-align: center;
      pointer-events: none;

      small {
        font: var(--mat-sys-label-small);
        color: var(--mat-sys-on-surface-variant);
      }
      strong {
        font: var(--mat-sys-title-medium);
        font-variant-numeric: tabular-nums;
      }
    }
    .tip {
      position: absolute;
      z-index: 3;
      transform: translate(-50%, calc(-100% - 6px));
      display: grid;
      gap: 2px;
      min-width: 170px;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--mat-sys-inverse-surface);
      color: var(--mat-sys-inverse-on-surface);
      font: var(--mat-sys-body-small);
      box-shadow: var(--mat-sys-level2);
      pointer-events: none;

      .ar {
        display: block;
      }
    }
    .tip .amount {
      font-variant-numeric: tabular-nums;
    }
    .legend {
      display: grid;
      gap: 2px;
      margin: 0;
      padding: 0;
      list-style: none;

      li {
        display: grid;
        grid-template-columns: auto 1fr auto 52px;
        align-items: center;
        gap: 8px;
        padding: 3px 6px;
        border-radius: 6px;
        font: var(--mat-sys-body-small);
        outline: none;

        &.active {
          background: var(--mat-sys-surface-container-low);
        }
        &:focus-visible {
          box-shadow: inset 0 0 0 2px var(--mat-sys-primary);
        }
      }
    }
    .swatch {
      width: 10px;
      height: 10px;
      border-radius: 3px;
      print-color-adjust: exact;
      -webkit-print-color-adjust: exact;
    }
    .name {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;

      .ar {
        display: inline;
        margin-left: 6px;
        color: var(--mat-sys-on-surface-variant);
      }
    }
    .amount,
    .percent {
      text-align: end;
      white-space: nowrap;
      font-variant-numeric: tabular-nums;
    }
    .percent {
      color: var(--mat-sys-on-surface-variant);
    }
    .none {
      margin: 24px 0;
      text-align: center;
      color: var(--mat-sys-on-surface-variant);
    }
    @media print {
      .tip {
        display: none;
      }
    }
  `,
})
export class SpendingPie {
  readonly heading = input.required<string>();
  readonly headingAr = input<string>('');
  readonly slices = input.required<PieSlice[]>();
  // What the share is of, said in the tooltip: "of all spending".
  readonly shareOf = input<string>('of the total');
  readonly empty = input<string>('Nothing for these days.');

  protected readonly size = size;
  protected readonly hovered = signal<number | null>(null);

  // Only what was spent; nothing and less than nothing have no slice.
  protected readonly total = computed(() => this.slices().reduce((sum, s) => sum + (s.amount > 0 ? s.amount : 0), 0));

  protected readonly drawn = computed<DrawnSlice[]>(() => {
    const total = this.total();
    if (total <= 0) {
      return [];
    }
    const largestFirst = this.slices()
      .filter((s) => s.amount > 0)
      .sort((a, b) => b.amount - a.amount);
    const kept = largestFirst.length > mostSlices ? largestFirst.slice(0, mostSlices - 1) : largestFirst;
    const rest = largestFirst.slice(kept.length);
    const slices: (PieSlice & { color: string; count?: number })[] = kept.map((s, i) => ({ ...s, color: s.color ?? pieColors[i % pieColors.length] }));
    if (rest.length) {
      slices.push({ key: 'other', en: 'Other', ar: 'أخرى', amount: rest.reduce((sum, s) => sum + s.amount, 0), color: otherColor, count: rest.length });
    }

    let start = -Math.PI / 2;
    return slices.map((s) => {
      const sweep = (s.amount / total) * Math.PI * 2;
      const end = start + sweep;
      const middle = start + sweep / 2;
      const drawn: DrawnSlice = {
        ...s,
        percent: (s.amount / total) * 100,
        path: SpendingPie.arc(start, end),
        tipX: ((centre + ((outer + inner) / 2) * Math.cos(middle)) / size) * 100,
        tipY: ((centre + ((outer + inner) / 2) * Math.sin(middle)) / size) * 100,
      };
      start = end;
      return drawn;
    });
  });

  protected readonly tip = computed(() => {
    const i = this.hovered();
    return i === null ? null : (this.drawn()[i] ?? null);
  });

  protected describe(): string {
    const slices = this.drawn().map((s) => `${s.en} ${s.amount.toFixed(2)} L.E, ${s.percent.toFixed(1)}%`);
    return `${this.heading()}: ${slices.join('; ')}`;
  }

  // A ring's slice from one angle to another, clockwise. The whole ring is drawn as two halves,
  // since one arc cannot end where it starts.
  private static arc(from: number, to: number): string {
    const at = (angle: number, radius: number) => `${round(centre + radius * Math.cos(angle))} ${round(centre + radius * Math.sin(angle))}`;
    if (to - from >= Math.PI * 2 - 1e-9) {
      const ring = (radius: number, sweep: 0 | 1) =>
        `M ${at(from, radius)} A ${radius} ${radius} 0 1 ${sweep} ${at(from + Math.PI, radius)} A ${radius} ${radius} 0 1 ${sweep} ${at(from, radius)} Z`;
      return `${ring(outer, 1)} ${ring(inner, 0)}`;
    }
    const large = to - from > Math.PI ? 1 : 0;
    return [
      `M ${at(from, outer)}`,
      `A ${outer} ${outer} 0 ${large} 1 ${at(to, outer)}`,
      `L ${at(to, inner)}`,
      `A ${inner} ${inner} 0 ${large} 0 ${at(from, inner)}`,
      'Z',
    ].join(' ');
  }
}

function round(value: number): number {
  return Math.round(value * 1000) / 1000;
}
