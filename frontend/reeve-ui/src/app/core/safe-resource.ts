import { ResourceRef, Signal, computed } from '@angular/core';

/**
 * A resource whose value() is undefined, rather than throwing, while the request has failed.
 * Angular resources throw from value() in the error state; with templates and computed signals
 * reading value() everywhere, one failed request would otherwise break the whole page instead of
 * showing the error. The failure is still available through error().
 */
export interface SafeResource<T> {
  readonly value: Signal<T | undefined>;
  readonly error: Signal<unknown>;
  readonly isLoading: Signal<boolean>;
  hasValue(): boolean;
  reload(): boolean;
  set(value: T): void;
}

export function safeResource<T>(ref: ResourceRef<T | undefined>): SafeResource<T> {
  return {
    value: computed(() => (ref.hasValue() ? ref.value() : undefined)),
    error: ref.error,
    isLoading: ref.isLoading,
    hasValue: () => ref.hasValue(),
    reload: () => ref.reload(),
    set: (value) => ref.set(value),
  };
}
