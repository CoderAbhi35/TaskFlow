import { HttpInterceptorFn } from '@angular/common/http';

export const CORRELATION_HEADER = 'X-Correlation-ID';

/**
 * Tags every API request with a correlation ID, so a failure shown in the UI can be matched to the
 * API's log lines (and, from Phase 10, its traces).
 */
export const correlationInterceptor: HttpInterceptorFn = (request, next) =>
  request.url.startsWith('/api/') && !request.headers.has(CORRELATION_HEADER)
    ? next(request.clone({ setHeaders: { [CORRELATION_HEADER]: newCorrelationId() } }))
    : next(request);

export function newCorrelationId(): string {
  return crypto.randomUUID().replaceAll('-', '');
}
