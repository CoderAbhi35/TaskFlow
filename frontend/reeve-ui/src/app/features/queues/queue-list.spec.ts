import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { QueueStats } from '../../core/models';
import { QueueList } from './queue-list';

function queue(jobType: string, counts: Partial<QueueStats> = {}): QueueStats {
  return {
    jobType,
    enabled: true,
    ready: 0,
    scheduled: 0,
    queued: 0,
    running: 0,
    deadLettered: 0,
    oldestWaitingAgeSeconds: null,
    ...counts,
  };
}

describe('QueueList', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('counts queued jobs as waiting, so a stalled Kafka queue does not look empty', async () => {
    // Every worker stopped: the dispatcher has published the jobs, so they are queued, not ready.
    const fixture = TestBed.createComponent(QueueList);
    TestBed.tick();
    http.expectOne('/api/v1/queues').flush([
      queue('PROCESS_IMAGE', { queued: 5, oldestWaitingAgeSeconds: 120 }),
      queue('GENERATE_REPORT', { ready: 2, running: 1 }),
    ]);
    await fixture.whenStable();

    const page = fixture.nativeElement as HTMLElement;
    expect(page.querySelector('.page-header')!.textContent).toContain('7 waiting, 1 running');
    expect(page.textContent).toContain('Longest wait');
    const stalled = [...page.querySelectorAll('tbody tr')].find((r) => r.textContent!.includes('PROCESS_IMAGE'))!;
    expect(stalled.textContent).toContain('2m 0s');
  });
});
