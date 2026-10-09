import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ThroughputBucket } from '../core/models';

export interface ChartBar {
  x: number;
  width: number;
  succeededHeight: number;
  failedHeight: number;
  label: string;
}

const WIDTH = 600;
const HEIGHT = 140;
const GAP = 1;

/** Stacked bars (succeeded, then failed on top) for each bucket. Plain SVG: no chart library. */
export function toBars(series: ThroughputBucket[], width = WIDTH, height = HEIGHT): { bars: ChartBar[]; max: number } {
  const max = Math.max(1, ...series.map((b) => b.succeeded + b.failed));
  const slot = series.length ? width / series.length : width;
  const bars = series.map((bucket, i) => ({
    x: i * slot,
    width: Math.max(1, slot - GAP),
    succeededHeight: (bucket.succeeded / max) * height,
    failedHeight: (bucket.failed / max) * height,
    label: `${new Date(bucket.start).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}: ${bucket.succeeded} succeeded, ${bucket.failed} failed`,
  }));
  return { bars, max };
}

@Component({
  selector: 'app-throughput-chart',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="chart">
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" preserveAspectRatio="none" role="img"
           [attr.aria-label]="'Jobs finished per ' + bucketMinutes() + ' min, peak ' + chart().max">
        @for (bar of chart().bars; track bar.x) {
          <g>
            <title>{{ bar.label }}</title>
            <rect class="bar-succeeded" [attr.x]="bar.x" [attr.width]="bar.width"
                  [attr.y]="height - bar.succeededHeight" [attr.height]="bar.succeededHeight" />
            <rect class="bar-failed" [attr.x]="bar.x" [attr.width]="bar.width"
                  [attr.y]="height - bar.succeededHeight - bar.failedHeight" [attr.height]="bar.failedHeight" />
          </g>
        }
      </svg>
      <figcaption class="muted">
        <span class="legend legend-succeeded">Succeeded</span>
        <span class="legend legend-failed">Failed</span>
        <span>Per {{ bucketMinutes() }} min · peak {{ chart().max }}</span>
      </figcaption>
    </figure>
  `,
})
export class ThroughputChart {
  readonly series = input.required<ThroughputBucket[]>();
  readonly bucketMinutes = input(1);

  protected readonly width = WIDTH;
  protected readonly height = HEIGHT;
  protected readonly chart = computed(() => toBars(this.series()));
}
