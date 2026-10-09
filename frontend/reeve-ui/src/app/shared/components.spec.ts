import { TestBed } from '@angular/core/testing';
import { FormControl } from '@angular/forms';
import { jsonObjectValidator } from '../features/jobs/job-submit';
import { StatusBadge } from './status-badge';
import { toBars } from './throughput-chart';

describe('StatusBadge', () => {
  it('shows a readable label and a tone for the status', async () => {
    const fixture = TestBed.createComponent(StatusBadge);
    fixture.componentRef.setInput('status', 'DEAD_LETTERED');
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent?.trim()).toBe('Dead lettered');
    expect(host.getAttribute('data-tone')).toBe('danger');
  });
});

describe('toBars', () => {
  it('scales bars to the busiest bucket and stacks failures on successes', () => {
    const { bars, max } = toBars(
      [
        { start: '2026-09-24T12:00:00Z', succeeded: 4, failed: 0 },
        { start: '2026-09-24T12:01:00Z', succeeded: 6, failed: 2 },
      ],
      200,
      100,
    );

    expect(max).toBe(8);
    expect(bars[0].x).toBe(0);
    expect(bars[1].x).toBe(100);
    expect(bars[1].succeededHeight).toBe(75);
    expect(bars[1].failedHeight).toBe(25);
    expect(bars[0].label).toContain('4 succeeded');
  });

  it('handles an empty window', () => {
    expect(toBars([]).bars).toEqual([]);
  });
});

describe('jsonObjectValidator', () => {
  const validate = (text: string) => jsonObjectValidator(new FormControl(text, { nonNullable: true }));

  it('accepts objects and empty input', () => {
    expect(validate('{"a": 1}')).toBeNull();
    expect(validate('   ')).toBeNull();
  });

  it('rejects other JSON values and broken JSON', () => {
    expect(validate('[1, 2]')?.['jsonObject']).toContain('JSON object');
    expect(validate('null')?.['jsonObject']).toContain('JSON object');
    expect(validate('{"a": ')?.['jsonObject']).toContain('Invalid JSON');
  });
});
