(function () {
  var C = window.TunProxyConsole;

  function buildItem(entry) {
    return {
      ex: entry.ex,
      id: entry.id,
      level: entry.level || 'INF',
      message: entry.message || '',
      text: entry.time + ' [' + (entry.level || 'INF') + '] ' + (entry.message || '') + (entry.ex ? ' ' + entry.ex : ''),
      time: entry.time
    };
  }

  C.mountPage({
    pageId: 'logs',
    setup: function () {
      var maxLines = 1000;
      var afterId = Vue.ref(0);
      var paused = Vue.ref(false);
      var pendingLines = Vue.ref([]);
      var lines = Vue.ref([]);
      var filterText = Vue.ref('[CONN]');
      var lastUpdate = Vue.ref(C.t('Page.Logs.Waiting'));
      var timer = null;
      var terminalRef = Vue.ref(null);

      var filterOptions = Vue.computed(function () {
        return [
          { label: C.t('Page.Logs.FilterAll'), value: '' },
          { label: C.t('Page.Logs.FilterConnections'), value: '[CONN]' },
          { label: C.t('Page.Logs.FilterDns'), value: '[DNS ]' },
          { label: C.t('Page.Logs.FilterWarnings'), value: 'WRN' },
          { label: C.t('Page.Logs.FilterErrors'), value: 'ERR' }
        ];
      });

      var visibleLines = Vue.computed(function () {
        var text = filterText.value.toLowerCase();
        if (!text) return lines.value;
        return lines.value.filter(function (line) {
          return line.text.toLowerCase().indexOf(text) >= 0;
        });
      });

      var counts = Vue.computed(function () {
        return {
          all: lines.value.length,
          conn: lines.value.filter(function (line) { return line.text.indexOf('[CONN]') >= 0; }).length,
          dns: lines.value.filter(function (line) { return line.text.indexOf('[DNS') >= 0; }).length,
          warnings: lines.value.filter(function (line) { return line.level === 'WRN'; }).length,
          errors: lines.value.filter(function (line) { return line.level === 'ERR' || line.level === 'FTL'; }).length
        };
      });

      var countCards = Vue.computed(function () {
        return [
          { label: C.t('Page.Logs.FilterAll'), value: counts.value.all, color: '' },
          { label: C.t('Page.Logs.FilterWarnings'), value: counts.value.warnings, color: 'var(--tp-amber)' },
          { label: C.t('Page.Logs.FilterConnections'), value: counts.value.conn, color: '' },
          { label: C.t('Page.Logs.FilterErrors'), value: counts.value.errors, color: counts.value.errors > 0 ? 'var(--tp-red)' : 'var(--tp-green)' }
        ];
      });

      var sidebarLines = Vue.computed(function () {
        return [
          { label: C.t('Page.Logs.FilterPlaceholder'), value: filterText.value || C.t('Page.Logs.FilterAll') },
          { label: C.t('Shared.RefreshAt').split('|')[0].replace('{0}', '2'), value: lastUpdate.value }
        ];
      });

      function appendItems(items) {
        if (!items.length) return;
        lines.value = lines.value.concat(items).slice(-maxLines);
        Vue.nextTick(function () {
          if (!filterText.value && terminalRef.value) {
            terminalRef.value.scrollTop = terminalRef.value.scrollHeight;
          }
        });
      }

      function poll() {
        window.TunProxyApi.getJson('/api/logs?after=' + afterId.value)
          .then(function (entries) {
            if (!entries || entries.length === 0) return;
            afterId.value = entries[entries.length - 1].id;
            lastUpdate.value = C.currentTime();
            var items = entries.map(buildItem);
            if (paused.value) {
              pendingLines.value = pendingLines.value.concat(items);
            } else {
              appendItems(items);
            }
          })
          .catch(function () {
            lastUpdate.value = C.t('Page.Logs.Reconnecting');
          });
      }

      function togglePause() {
        paused.value = !paused.value;
        if (!paused.value && pendingLines.value.length > 0) {
          appendItems(pendingLines.value);
          pendingLines.value = [];
        }
      }

      function clearLogs() {
        lines.value = [];
        pendingLines.value = [];
      }

      function scrollToBottom() {
        Vue.nextTick(function () {
          if (terminalRef.value) terminalRef.value.scrollTop = terminalRef.value.scrollHeight;
        });
        if (paused.value) togglePause();
      }

      Vue.onMounted(function () {
        poll();
        timer = window.setInterval(poll, 2000);
      });

      Vue.onBeforeUnmount(function () {
        if (timer) window.clearInterval(timer);
      });

      return {
        C: C,
        clearLogs: clearLogs,
        countCards: countCards,
        counts: counts,
        filterOptions: filterOptions,
        filterText: filterText,
        lastUpdate: lastUpdate,
        lines: lines,
        paused: paused,
        pendingLines: pendingLines,
        scrollToBottom: scrollToBottom,
        sidebarLines: sidebarLines,
        terminalRef: terminalRef,
        togglePause: togglePause,
        visibleLines: visibleLines
      };
    },
    template: `
      <tp-shell
        v-model:active-page="activePage"
        :culture="culture"
        :eyebrow="t('Nav.Logs') + ' / ' + t('Page.Logs.Heading')"
        :mobile-options="mobileOptions"
        :pages="pages"
        :sidebar-lines="sidebarLines"
        :title="t('Page.Logs.Heading')"
        @change-culture="setCulture">
        <template #actions>
          <a-button :type="paused ? 'primary' : 'default'" @click="togglePause">{{ paused ? t('Page.Logs.Resume') : t('Page.Logs.Pause') }}</a-button>
          <a-button danger @click="clearLogs">{{ t('Page.Logs.Clear') }}</a-button>
          <a-button type="primary" @click="scrollToBottom">{{ t('Page.Logs.ScrollToLatest') }}</a-button>
        </template>

        <div class="tp-log-layout">
          <section class="tp-section">
            <div class="tp-log-toolbar">
              <a-segmented v-model:value="filterText" :options="filterOptions"></a-segmented>
              <div class="tp-toolbar">
                <a-input v-model:value="filterText" :placeholder="t('Page.Logs.FilterPlaceholder')" style="width: 240px"></a-input>
                <a-tag color="blue">{{ t('Page.Logs.UpdatedAt').replace('{0}', lastUpdate) }}</a-tag>
              </div>
            </div>

            <div ref="terminalRef" class="tp-terminal">
              <div class="tp-terminal-head"><span>tunproxy-.log</span><span>max 1000 lines · filter {{ filterText || t('Page.Logs.FilterAll') }}</span></div>
              <div v-if="visibleLines.length === 0" class="tp-empty-state" style="background: transparent; border-color: #253044">
                <div class="tp-section-title">{{ C.htmlText(t('Page.Logs.EmptyHtml')) }}</div>
              </div>
              <p v-for="line in visibleLines" :key="line.id" class="tp-log-line">
                <span class="tp-log-time">{{ line.time }}</span>
                <span :class="'tp-log-' + line.level"> [{{ line.level }}]</span>
                <span> {{ line.message }}</span>
                <span v-if="line.ex" class="tp-log-ex">{{ line.ex }}</span>
              </p>
            </div>
          </section>

          <aside>
            <section class="tp-section">
              <div class="tp-section-title">{{ t('Page.Logs.Title') }}</div>
              <div class="tp-two-grid" style="grid-template-columns:1fr 1fr;margin-top:12px">
                <div v-for="card in countCards" :key="card.label" class="tp-log-count-card">
                  <span class="tp-muted">{{ card.label }}</span>
                  <span class="tp-count-value" :style="{ color: card.color || '#18202f' }">{{ card.value }}</span>
                </div>
              </div>
            </section>

            <section class="tp-section">
              <div class="tp-section-title">{{ t('Page.Status.Heading') }}</div>
              <tp-kv-list :items="[
                { label: t('Page.Status.Badge.Running'), value: t('Page.Status.Badge.Running') },
                { label: t('Page.Logs.FilterAll'), value: counts.all },
                { label: t('Page.Logs.FilterWarnings'), value: counts.warnings },
                { label: t('Page.Logs.FilterErrors'), value: counts.errors }
              ]"></tp-kv-list>
            </section>
          </aside>
        </div>

        <a-float-button class="tp-fab" @click="scrollToBottom" :tooltip="t('Page.Logs.ScrollToLatest')"></a-float-button>
      </tp-shell>
    `
  });
})();
