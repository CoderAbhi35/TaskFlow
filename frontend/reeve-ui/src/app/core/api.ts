import { HttpClient, HttpParams, httpResource } from '@angular/common/http';
import { Injectable, Signal, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { safeResource } from './safe-resource';
import {
  CreateJobRequest,
  CreateScheduleRequest,
  Job,
  JobAttempt,
  JobFilters,
  JobSummary,
  Overview,
  Page,
  QueueStats,
  Schedule,
  Worker,
} from './models';

export const API_BASE = '/api/v1';

/**
 * Reeve API client. Reads are exposed as signal-based resources (call them from a component's
 * field initialisers); writes return observables.
 */
@Injectable({ providedIn: 'root' })
export class ReeveApi {
  private readonly http = inject(HttpClient);

  // ---- reads (resources) --------------------------------------------------------------------

  overview(windowMinutes: Signal<number>) {
    return safeResource(httpResource<Overview>(() => ({ url: `${API_BASE}/stats/overview`, params: { windowMinutes: windowMinutes() } })));
  }

  job(id: Signal<string>) {
    return safeResource(httpResource<Job>(() => `${API_BASE}/jobs/${id()}`));
  }

  attempts(id: Signal<string>) {
    return safeResource(httpResource<JobAttempt[]>(() => `${API_BASE}/jobs/${id()}/attempts`));
  }

  workers() {
    return safeResource(httpResource<Worker[]>(() => `${API_BASE}/workers`));
  }

  queues() {
    return safeResource(httpResource<QueueStats[]>(() => `${API_BASE}/queues`));
  }

  schedules() {
    return safeResource(httpResource<Schedule[]>(() => `${API_BASE}/schedules`));
  }

  /** The health endpoint answers in plain text: Healthy, Degraded or Unhealthy (503). */
  health() {
    return safeResource<string>(httpResource.text(() => `${API_BASE}/health`));
  }

  // ---- paged search ------------------------------------------------------------------------

  searchJobs(filters: JobFilters, cursor?: string | null): Observable<Page<JobSummary>> {
    return this.http.get<Page<JobSummary>>(`${API_BASE}/jobs`, { params: searchParams(filters, cursor) });
  }

  // ---- writes ------------------------------------------------------------------------------

  createJob(request: CreateJobRequest): Observable<Job> {
    return this.http.post<Job>(`${API_BASE}/jobs`, request);
  }

  cancelJob(id: string): Observable<Job> {
    return this.http.post<Job>(`${API_BASE}/jobs/${id}/cancel`, null);
  }

  retryJob(id: string): Observable<Job> {
    return this.http.post<Job>(`${API_BASE}/jobs/${id}/retry`, null);
  }

  createSchedule(request: CreateScheduleRequest): Observable<Schedule> {
    return this.http.post<Schedule>(`${API_BASE}/schedules`, request);
  }

  pauseSchedule(id: string): Observable<Schedule> {
    return this.http.post<Schedule>(`${API_BASE}/schedules/${id}/pause`, null);
  }

  resumeSchedule(id: string): Observable<Schedule> {
    return this.http.post<Schedule>(`${API_BASE}/schedules/${id}/resume`, null);
  }

  deleteSchedule(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/schedules/${id}`);
  }
}

/** Builds the query string for job search; statuses repeat (`?status=A&status=B`). */
export function searchParams(filters: JobFilters, cursor?: string | null): HttpParams {
  let params = new HttpParams();
  for (const status of filters.status ?? []) params = params.append('status', status);
  if (filters.jobType) params = params.set('jobType', filters.jobType);
  if (filters.priority) params = params.set('priority', filters.priority);
  if (filters.limit) params = params.set('limit', filters.limit);
  if (cursor) params = params.set('cursor', cursor);
  return params;
}
