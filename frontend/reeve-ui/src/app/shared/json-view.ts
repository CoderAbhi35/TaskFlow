import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-json-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<pre class="json">{{ text() }}</pre>`,
})
export class JsonView {
  readonly value = input<unknown>();
  protected readonly text = computed(() => JSON.stringify(this.value() ?? {}, null, 2));
}
