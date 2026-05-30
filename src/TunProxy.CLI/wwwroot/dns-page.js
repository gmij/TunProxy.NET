(function () {
  var C = window.TunProxyConsole;

  C.mountPage({
    pageId: 'dns',
    setup: function () {
      var records = Vue.ref([]);
      var isTunMode = Vue.ref(false);
      var query = Vue.ref('');
      var loading = Vue.ref(true);
      var lastUpdate = Vue.ref('-');
      var timer = null;

      var filteredRecords = Vue.computed(function () {
        var text = query.value.toLowerCase();
        var entries = records.value.slice();
        if (text) {
          entries = entries.filter(function (record) {
            return String(record.ipAddress || '').toLowerCase().indexOf(text) >= 0 ||
              String(record.hostname || '').toLowerCase().indexOf(text) >= 0 ||
              String(record.route || '').toLowerCase().indexOf(text) >= 0 ||
              String(record.reason || '').toLowerCase().indexOf(text) >= 0;
          });
        }
        return entries;
      });

      var columns = Vue.computed(function () {
        return [
          {
            title: C.t('Page.Dns.ResolvedDomain'),
            key: 'hostname',
            dataIndex: 'hostname',
            sorter: function (a, b) { return String(a.hostname || '').localeCompare(String(b.hostname || '')); },
            defaultSortOrder: 'ascend'
          },
          {
            title: C.t('Page.Dns.IpAddress'),
            key: 'ipAddress',
            dataIndex: 'ipAddress',
            sorter: function (a, b) { return String(a.ipAddress || '').localeCompare(String(b.ipAddress || '')); }
          },
          {
            title: C.t('Page.Dns.SeenCount'),
            key: 'seenCount',
            dataIndex: 'seenCount',
            width: 100,
            sorter: function (a, b) { return Number(a.seenCount || 0) - Number(b.seenCount || 0); }
          },
          {
            title: C.t('Page.Dns.Route'),
            key: 'route',
            width: 200
          },
          {
            title: C.t('Page.Dns.Reason'),
            key: 'reason',
            dataIndex: 'reason',
            width: 120
          },
          {
            title: C.t('Page.Dns.LastActive'),
            key: 'lastActiveUtc',
            dataIndex: 'lastActiveUtc',
            width: 110,
            sorter: function (a, b) {
              return new Date(a.lastActiveUtc || 0).getTime() - new Date(b.lastActiveUtc || 0).getTime();
            }
          },
          {
            title: '',
            key: 'action',
            width: 80
          }
        ];
      });

      var summary = Vue.computed(function () {
        var proxy = records.value.filter(function (record) { return record.route === 'PROXY'; }).length;
        var direct = records.value.filter(function (record) { return record.route === 'DIRECT'; }).length;
        var cached = records.value.filter(function (record) { return record.isDnsCached; }).length;
        return [
          { label: C.t('Page.Dns.Heading'), value: records.value.length, sub: records.value.length ? C.t('Page.Dns.Heading') : C.t('Page.Dns.Empty') },
          { label: C.t('Page.Dns.RouteProxy'), value: proxy, sub: 'GFW / Geo / Default' },
          { label: C.t('Page.Dns.RouteDirect'), value: direct, sub: C.t('Page.Dns.LegendDirect') },
          { label: 'DNS cache', value: cached, sub: C.t('Page.Dns.ClearSearch') }
        ];
      });

      var sidebarLines = Vue.computed(function () {
        return [
          { label: C.t('Page.Config.SystemProxyMode'), value: isTunMode.value ? C.t('Page.Config.SystemProxyMode.Tun') : C.t('Mode.Proxy') },
          { label: C.t('Shared.RefreshAt').split('|')[0].replace('{0}', '10'), value: lastUpdate.value }
        ];
      });

      var emptyMessage = Vue.computed(function () {
        return isTunMode.value
          ? '接口当前返回 0 条记录。产生新的 DNS/TUN 流量后会自动刷新。'
          : C.htmlText(C.t('Page.Dns.ProxyModeNoticeHtml'));
      });

      function loadData() {
        loading.value = true;
        return window.TunProxyApi.getJson('/api/status')
          .then(function (status) {
            isTunMode.value = status.mode === 'tun';
            if (!isTunMode.value) {
              records.value = [];
              lastUpdate.value = C.currentTime();
              return null;
            }
            return window.TunProxyApi.getJson('/api/dns-records').then(function (payload) {
              records.value = Array.isArray(payload) ? payload : [];
              lastUpdate.value = C.currentTime();
            });
          })
          .finally(function () {
            loading.value = false;
          });
      }

      function clearDnsCache(record) {
        if (!record || !record.ipAddress) return;
        var url = '/api/dns-cache?ip=' + encodeURIComponent(record.ipAddress);
        if (record.hostname) url += '&domain=' + encodeURIComponent(record.hostname);
        window.TunProxyApi.delete(url).then(loadData);
      }

      Vue.onMounted(function () {
        loadData();
        timer = window.setInterval(loadData, 10000);
      });

      Vue.onBeforeUnmount(function () {
        if (timer) window.clearInterval(timer);
      });

      return {
        C: C,
        clearDnsCache: clearDnsCache,
        columns: columns,
        filteredRecords: filteredRecords,
        emptyMessage: emptyMessage,
        isTunMode: isTunMode,
        lastUpdate: lastUpdate,
        loading: loading,
        query: query,
        records: records,
        routeColor: C.routeColor,
        routeLabel: C.routeLabel,
        sidebarLines: sidebarLines,
        summary: summary
      };
    },
    template: `
      <tp-shell
        v-model:active-page="activePage"
        :culture="culture"
        :eyebrow="t('Nav.Dns') + ' / ' + t('Page.Dns.Heading')"
        :mobile-options="mobileOptions"
        :pages="pages"
        :sidebar-lines="sidebarLines"
        :title="t('Page.Dns.Heading')"
        @change-culture="setCulture">
        <template #actions>
          <a-input v-model:value="query" :placeholder="t('Page.Dns.SearchPlaceholder')" style="width: 300px"></a-input>
          <a-button @click="query = ''">{{ t('Page.Dns.ClearSearch') }}</a-button>
        </template>

        <a-alert v-if="!isTunMode && !loading" type="info" show-icon style="margin-bottom: 14px">
          <template #message>{{ t('Page.Dns.Title') }}</template>
          <template #description>{{ C.htmlText(t('Page.Dns.ProxyModeNoticeHtml')) }}</template>
        </a-alert>

        <div class="tp-four-grid" style="margin-bottom: 16px">
          <div v-for="item in summary" :key="item.label" class="tp-summary-card">
            <div class="tp-muted">{{ item.label }}</div>
            <div class="tp-summary-value">{{ item.value }}</div>
            <div class="tp-muted">{{ item.sub }}</div>
          </div>
        </div>

        <div class="tp-page-grid">
          <section class="tp-section">
            <div class="tp-section-head">
              <div><div class="tp-section-title">{{ t('Page.Dns.Heading') }}</div><div class="tp-muted">{{ C.format('Shared.RefreshAt', 10, lastUpdate) }}</div></div>
              <a-tag color="blue">{{ isTunMode ? t('Page.Config.SystemProxyMode.Tun') : t('Mode.Proxy') }}</a-tag>
            </div>
            <div class="tp-legend">
              <span class="tp-legend-item"><a-tag>{{ t('Page.Dns.RouteDirect') }}</a-tag>{{ t('Page.Dns.LegendDirect') }}</span>
              <span class="tp-legend-item"><a-tag color="success">{{ t('Page.Dns.RouteProxy') }}</a-tag>{{ t('Page.Dns.LegendGfw') }}</span>
              <span class="tp-legend-item"><a-tag color="cyan">{{ t('Page.Dns.RouteProxy') }}</a-tag>{{ t('Page.Dns.LegendGeo') }}</span>
              <span class="tp-legend-item"><a-tag color="warning">{{ t('Page.Dns.RouteProxy') }}</a-tag>{{ t('Page.Dns.LegendDefault') }}</span>
            </div>

            <a-table
              :data-source="filteredRecords"
              :columns="columns"
              :loading="loading"
              :pagination="false"
              size="small"
              :row-key="(r) => r.ipAddress + '_' + r.hostname"
              :scroll="{ x: 920 }"
            >
              <template #bodyCell="{ column, record }">
                <template v-if="column.key === 'hostname'">
                  <strong>{{ record.hostname || '-' }}</strong>
                </template>
                <template v-else-if="column.key === 'ipAddress'">
                  <span class="tp-code">{{ record.ipAddress }}</span>
                </template>
                <template v-else-if="column.key === 'route'">
                  <span class="tp-toolbar" style="justify-content:flex-start;gap:4px">
                    <a-tag :color="routeColor(record)">{{ routeLabel(record) }}</a-tag>
                    <a-tag v-if="record.isDnsCached" color="blue">DNS cache</a-tag>
                    <a-tag v-if="record.isPrivateIp">{{ t('Page.Dns.PrivateIp') }}</a-tag>
                  </span>
                </template>
                <template v-else-if="column.key === 'reason'">
                  <span class="tp-code">{{ record.reason || '-' }}</span>
                </template>
                <template v-else-if="column.key === 'lastActiveUtc'">
                  <span class="tp-code">{{ C.dateTime(record.lastActiveUtc) }}</span>
                </template>
                <template v-else-if="column.key === 'action'">
                  <a-button v-if="record.isDnsCached" danger size="small" @click="clearDnsCache(record)">Clear</a-button>
                </template>
              </template>
              <template #emptyText>
                <div class="tp-empty-state">
                  <div class="tp-section-title">{{ t('Page.Dns.Empty') }}</div>
                  <div class="tp-muted">{{ emptyMessage }}</div>
                </div>
              </template>
            </a-table>
          </section>

          <aside>
            <section class="tp-section">
              <div class="tp-section-title">{{ t('Page.Dns.Route') }}</div>
              <div class="tp-route-map" style="margin-top: 12px">
                <div class="tp-route-node active"><strong>1. DNS cache</strong><span class="tp-muted">{{ t('Page.Dns.PrivateIp') }} / FakeIP</span></div>
                <div class="tp-route-node"><strong>2. {{ t('Page.Dns.RouteDirect') }}</strong><span class="tp-muted">{{ t('Page.Dns.LegendDirect') }}</span></div>
                <div class="tp-route-node"><strong>3. {{ t('Page.Dns.RouteProxy') }}</strong><span class="tp-muted">{{ t('Page.Dns.LegendGfw') }} / {{ t('Page.Dns.LegendGeo') }}</span></div>
              </div>
            </section>
            <section class="tp-section">
              <div class="tp-section-title">DNS</div>
              <tp-kv-list :items="[
                { label: t('Page.Config.SystemProxyMode'), value: isTunMode ? t('Page.Config.SystemProxyMode.Tun') : t('Mode.Proxy') },
                { label: 'Records', value: records.length },
                { label: t('Shared.RefreshAt').split('|')[0].replace('{0}', '10'), value: lastUpdate }
              ]"></tp-kv-list>
            </section>
            <section v-if="isTunMode" class="tp-section" style="margin-top:16px">
              <div class="tp-section-title" style="margin-bottom:8px">{{ t('Page.Dns.DoHNoticeTitle') }}</div>
              <div class="tp-muted" style="font-size:12px;line-height:1.6">{{ t('Page.Dns.DoHNoticeBody') }}</div>
            </section>
          </aside>
        </div>
      </tp-shell>
    `
  });
})();
