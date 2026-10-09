import { Pipe, PipeTransform } from '@angular/core';

/** 850 → "850 ms", 12_300 → "12.3 s", 125_000 → "2m 5s", 7_500_000 → "2h 5m". */
export function formatDuration(ms: number | null | undefined): string {
  if (ms == null || Number.isNaN(ms)) return '—';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 2 : 1)} s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${Math.round(seconds % 60)}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

/** Relative to `now`: "just now", "42 s ago", "5 min ago", "in 3 h", then a date. */
export function formatRelative(iso: string | null | undefined, now: number): string {
  if (!iso) return '—';
  const deltaSeconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(deltaSeconds);
  const suffix = (text: string) => (deltaSeconds < 0 ? `${text} ago` : `in ${text}`);

  if (abs < 5) return 'just now';
  if (abs < 60) return suffix(`${abs} s`);
  if (abs < 3600) return suffix(`${Math.round(abs / 60)} min`);
  if (abs < 86_400) return suffix(`${Math.round(abs / 3600)} h`);
  return new Date(iso).toLocaleString();
}

export function formatPercent(ratio: number | null | undefined): string {
  if (ratio == null) return '—';
  const percent = ratio * 100;
  return `${percent >= 99.95 || percent === 0 ? percent.toFixed(0) : percent.toFixed(1)}%`;
}

/** "DEAD_LETTERED" → "Dead lettered". */
export function humanize(value: string): string {
  const words = value.toLowerCase().replaceAll('_', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

@Pipe({ name: 'duration' })
export class DurationPipe implements PipeTransform {
  transform(ms: number | null | undefined): string {
    return formatDuration(ms);
  }
}

/** Pure pipe: pass a ticking `now` so the text stays current: `{{ at | relative: now() }}`. */
@Pipe({ name: 'relative' })
export class RelativeTimePipe implements PipeTransform {
  transform(iso: string | null | undefined, now: number): string {
    return formatRelative(iso, now);
  }
}

@Pipe({ name: 'percent1' })
export class PercentPipe implements PipeTransform {
  transform(ratio: number | null | undefined): string {
    return formatPercent(ratio);
  }
}

@Pipe({ name: 'humanize' })
export class HumanizePipe implements PipeTransform {
  transform(value: string | null | undefined): string {
    return value ? humanize(value) : '';
  }
}

/**
 * A short, distinguishing form of a job ID. IDs are UUIDv7, whose leading characters are a
 * timestamp shared by every job created in the same moment, so the tail (random bits) is used.
 */
export function shortId(id: string): string {
  return id.slice(-8);
}

@Pipe({ name: 'shortId' })
export class ShortIdPipe implements PipeTransform {
  transform(id: string): string {
    return shortId(id);
  }
}
