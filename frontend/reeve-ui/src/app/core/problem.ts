import { HttpErrorResponse } from '@angular/common/http';
import { Problem } from './models';

export interface ErrorView {
  message: string;
  /** Field-level validation messages, keyed by field name. */
  fields: Record<string, string[]>;
  /** Quote this when reporting a problem; it finds the request in the server logs. */
  correlationId?: string;
  status?: number;
}

/** Turns anything a request can fail with into something worth showing a user. */
export function describeError(error: unknown): ErrorView {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return { message: 'Cannot reach the Reeve API. Is it running?', fields: {}, status: 0 };
    }

    const problem = asProblem(error.error);
    if (problem) {
      const message =
        problem.status === 429
          ? `${problem.detail ?? 'Too many requests.'}`
          : [problem.title, problem.detail].filter(Boolean).join(': ') || error.message;
      return { message, fields: problem.errors ?? {}, correlationId: problem.correlationId, status: error.status };
    }

    return { message: `${error.status} ${error.statusText || 'Request failed'}`, fields: {}, status: error.status };
  }

  if (error instanceof Error) return { message: error.message, fields: {} };
  return { message: 'Something went wrong.', fields: {} };
}

function asProblem(body: unknown): Problem | null {
  if (typeof body === 'string') {
    try {
      body = JSON.parse(body);
    } catch {
      return null;
    }
  }
  return body && typeof body === 'object' && ('title' in body || 'code' in body) ? (body as Problem) : null;
}
