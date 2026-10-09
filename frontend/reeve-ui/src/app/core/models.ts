// Typed mirrors of Reeve.Contracts. Enums travel as SNAKE_UPPER strings.

export const JOB_STATUSES = ['PENDING', 'QUEUED', 'RUNNING', 'SUCCEEDED', 'FAILED', 'CANCELLED', 'DEAD_LETTERED'] as const;
export type JobStatus = (typeof JOB_STATUSES)[number];

export const JOB_PRIORITIES = ['LOW', 'NORMAL', 'HIGH', 'CRITICAL'] as const;
export type JobPriority = (typeof JOB_PRIORITIES)[number];

export type JobAttemptStatus = 'RUNNING' | 'SUCCEEDED' | 'FAILED' | 'CANCELLED';
export type WorkerStatus = 'ACTIVE' | 'DRAINING' | 'OFFLINE';

/** Statuses a job can still leave. */
export const ACTIVE_STATUSES: readonly JobStatus[] = ['PENDING', 'QUEUED', 'RUNNING'];

export interface RetryPolicy {
  maxRetries: number;
  backoffSeconds: number;
}

export interface Job {
  id: string;
  jobType: string;
  status: JobStatus;
  priority: JobPriority;
  payload: unknown;
  retryPolicy: RetryPolicy;
  attemptCount: number;
  retryCount: number;
  idempotencyKey: string | null;
  lastError: string | null;
  scheduledAt: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
}

export type JobSummary = Omit<Job, 'payload' | 'retryPolicy' | 'retryCount' | 'idempotencyKey'>;

export interface JobAttempt {
  id: string;
  attemptNumber: number;
  workerId: string;
  status: JobAttemptStatus;
  startedAt: string;
  endedAt: string | null;
  durationMs: number | null;
  error: string | null;
}

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
}

export interface CreateJobRequest {
  jobType: string;
  priority?: JobPriority;
  payload?: unknown;
  retryPolicy?: RetryPolicy;
  scheduledAt?: string;
}

export interface JobFilters {
  status?: JobStatus[];
  jobType?: string;
  priority?: JobPriority;
  limit?: number;
}

export interface Worker {
  id: string;
  hostname: string;
  status: WorkerStatus;
  concurrency: number;
  supportedJobTypes: string[];
  registeredAt: string;
  lastHeartbeatAt: string;
  heartbeatAgeSeconds: number;
  isStale: boolean;
}

export interface QueueStats {
  jobType: string;
  enabled: boolean;
  ready: number;
  scheduled: number;
  queued: number;
  running: number;
  deadLettered: number;
  /** Oldest job that is due but not yet picked up (ready or queued). */
  oldestWaitingAgeSeconds: number | null;
}

export interface Schedule {
  id: string;
  name: string;
  jobType: string;
  cronExpression: string;
  timeZone: string;
  enabled: boolean;
  priority: JobPriority;
  payload: unknown;
  retryPolicy: RetryPolicy;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastJobId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateScheduleRequest {
  name: string;
  jobType: string;
  cronExpression: string;
  timeZone?: string;
  priority?: JobPriority;
  payload?: unknown;
}

export interface ThroughputBucket {
  start: string;
  succeeded: number;
  failed: number;
}

export interface Overview {
  windowMinutes: number;
  generatedAt: string;
  submitted: number;
  succeeded: number;
  failed: number;
  deadLettered: number;
  cancelled: number;
  failedAttempts: number;
  successRate: number | null;
  throughputPerMinute: number;
  p50DurationMs: number | null;
  p95DurationMs: number | null;
  backlog: { ready: number; scheduled: number; queued: number; running: number; deadLettered: number };
  workers: { active: number; draining: number; stale: number; offline: number; totalConcurrency: number };
  bucketMinutes: number;
  series: ThroughputBucket[];
}

export interface AuditEvent {
  id: string;
  occurredAt: string;
  actor: string;
  action: string;
  entityType: string;
  entityId: string;
  correlationId: string | null;
  details: unknown;
}

/** RFC 9457 problem document with Reeve's extensions. */
export interface Problem {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}
