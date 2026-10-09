import { Routes } from '@angular/router';
import { adminGuard, signedInGuard } from './core/auth-guards';
import { Shell } from './layout/shell';

export const routes: Routes = [
  { path: 'login', title: 'Sign in · Reeve', loadComponent: () => import('./features/login/login').then((m) => m.Login) },
  {
    path: '',
    component: Shell,
    canActivate: [signedInGuard],
    children: [
      { path: '', title: 'Overview · Reeve', loadComponent: () => import('./features/overview/overview').then((m) => m.Overview) },
      { path: 'jobs', title: 'Jobs · Reeve', loadComponent: () => import('./features/jobs/job-list').then((m) => m.JobList) },
      { path: 'jobs/:id', title: 'Job · Reeve', loadComponent: () => import('./features/jobs/job-detail').then((m) => m.JobDetail) },
      { path: 'failures', title: 'Failures · Reeve', loadComponent: () => import('./features/failures/failure-list').then((m) => m.FailureList) },
      { path: 'queues', title: 'Queues · Reeve', loadComponent: () => import('./features/queues/queue-list').then((m) => m.QueueList) },
      { path: 'workers', title: 'Workers · Reeve', loadComponent: () => import('./features/workers/worker-list').then((m) => m.WorkerList) },
      { path: 'schedules', title: 'Schedules · Reeve', loadComponent: () => import('./features/schedules/schedule-list').then((m) => m.ScheduleList) },
      {
        path: 'audit',
        title: 'Audit · Reeve',
        canActivate: [adminGuard],
        loadComponent: () => import('./features/audit/audit-list').then((m) => m.AuditList),
      },
      { path: '**', redirectTo: '' },
    ],
  },
];
