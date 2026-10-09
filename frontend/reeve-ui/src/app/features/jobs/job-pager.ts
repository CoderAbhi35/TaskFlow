import { Signal, computed, inject, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { ReeveApi } from '../../core/api';
import { safeResource } from '../../core/safe-resource';
import { JobFilters, JobSummary } from '../../core/models';

/**
 * Cursor-paged job search. The first page is a resource that reloads when the filters change;
 * "load more" appends further pages, which are discarded when the filters change.
 * Call from an injection context.
 */
export function jobPager(filters: Signal<JobFilters>) {
  const api = inject(ReeveApi);

  const firstPage = safeResource(rxResource({
    params: () => filters(),
    stream: ({ params }) => api.searchJobs(params),
  }));

  const morePages = linkedSignal<JobFilters, { items: JobSummary[]; nextCursor: string | null }[]>({
    source: filters,
    computation: () => [],
  });
  const loadingMore = signal(false);
  const loadMoreError = signal<unknown>(null);

  const items = computed(() => [...(firstPage.value()?.items ?? []), ...morePages().flatMap((p) => p.items)]);
  const nextCursor = computed(() => {
    const pages = morePages();
    return pages.length ? pages[pages.length - 1].nextCursor : (firstPage.value()?.nextCursor ?? null);
  });

  return {
    firstPage,
    items,
    nextCursor,
    loadingMore: loadingMore.asReadonly(),
    loadMoreError: loadMoreError.asReadonly(),

    loadMore(): void {
      const cursor = nextCursor();
      if (!cursor || loadingMore()) return;
      loadingMore.set(true);
      loadMoreError.set(null);
      api.searchJobs(filters(), cursor).subscribe({
        next: (page) => morePages.update((pages) => [...pages, page]),
        error: (error) => {
          loadMoreError.set(error);
          loadingMore.set(false);
        },
        complete: () => loadingMore.set(false),
      });
    },

    /** Refreshes only while a single page is shown, so auto-refresh never throws away pages the user loaded. */
    reload(): boolean {
      return morePages().length === 0 ? firstPage.reload() : false;
    },
  };
}
