import { HttpErrorResponse } from '@angular/common/http';
import { describeError } from './problem';

describe('describeError', () => {
  it('explains an unreachable API', () => {
    const view = describeError(new HttpErrorResponse({ status: 0 }));
    expect(view.message).toContain('Cannot reach');
  });

  it('uses the problem document and keeps field errors and the correlation ID', () => {
    const view = describeError(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'Validation failed',
          code: 'validation_failed',
          correlationId: 'abc123',
          errors: { jobType: ["Job type 'X' is not registered or is disabled."] },
        },
      }),
    );

    expect(view.message).toBe('Validation failed');
    expect(view.fields['jobType']).toHaveLength(1);
    expect(view.correlationId).toBe('abc123');
    expect(view.status).toBe(400);
  });

  it('parses problem documents delivered as text', () => {
    const view = describeError(
      new HttpErrorResponse({ status: 409, error: JSON.stringify({ title: 'Invalid state transition', detail: 'Job cannot be retried.' }) }),
    );
    expect(view.message).toBe('Invalid state transition: Job cannot be retried.');
  });

  it('shows the rate-limit detail on 429', () => {
    const view = describeError(
      new HttpErrorResponse({ status: 429, error: { status: 429, title: 'Too many requests', detail: 'Retry after 12 s.' } }),
    );
    expect(view.message).toBe('Retry after 12 s.');
  });

  it('falls back for non-problem errors', () => {
    expect(describeError(new HttpErrorResponse({ status: 502, statusText: 'Bad Gateway' })).message).toBe('502 Bad Gateway');
    expect(describeError(new Error('boom')).message).toBe('boom');
    expect(describeError('?').message).toBe('Something went wrong.');
  });
});
