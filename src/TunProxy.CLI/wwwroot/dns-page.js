(function () {
  var C = window.TunProxyConsole;

  C.mountPage({
    pageId: 'dns',
    setup: function () {
      var records = Vue.ref([]);
      var isTunMode = Vue.ref(false);
      var query = Vue.ref('');
      var refreshPipeline = C.createAsyncPipeline({ initialLoading: true });
      var lastUpdate = Vue.ref('-');
      var stopPolling = null;

      // Group raw records by hostname; each group becomes one display row.
      var groupedRecords = Vue.computed(function () {
        var map = {};
        records.value.forEach(function (r) {
          var key = (r.hostname || r.ipAddress || '').toLowerCase();
          if (!map[key]) {
            map[key] = {
              hostname: r.hostname,
              route: r.route,
              reason: r.reason,
              seenCount: 0,
              lastActiveUtc: r.lastActiveUtc,
              isDnsCached: false,
              isPrivateIp: r.isPrivateIp,
              _records: []
            };
          }
          var g = map[key];
          g._records.push(r);
          g.seenCount += (r.seenCount || 0);
          if (r.isDnsCached) g.isDnsCached = true;
          if (!g.lastActiveUtc || new Date(r.lastActiveUtc) > new Date(g.lastActiveUtc)) {
            g.lastActiveUtc = r.lastActiveUtc;
            g.route = r.route;
            g.reason = r.reason;
          }
        });
        return Object.values(map);
      });

      var filteredRecords = Vue.computed(function () {
        var text = query.value.toLowerCase();
        var entries = groupedRecords.value.slice();
        if (text) {
          entries = entries.filter(function (g) {
            var ips = g._records.map(function (r) { return r.ipAddress || ''; }).join(' ');
            return ips.toLowerCase().indexOf(text) >= 0 ||
              String(g.hostname || '').toLowerCase().indexOf(text) >= 0 ||
              String(g.route || '').toLowerCase().indexOf(text) >= 0 ||
              String(g.reason || '').toLowerCase().indexOf(text) >= 0;
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
            key: 'ipAddresses'
          },
          {
            title: C.t('Page.Dns.SeenCount'),
            key: 'seenCount',
            dataIndex: 'seenCount',
            width: 80,
            sorter: function (a, b) { return Number(a.seenCount || 0) - Number(b.seenCount || 0); }
          },
          {
            title: C.t('Page.Dns.Route'),
            key: 'route',
            width: 180
          },
          {
            title: C.t('Page.Dns.Reason'),
            key: 'reason',
            dataIndex: 'reason',
            width: 110
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
        var proxy = groupedRecords.value.filter(function (g) { return g.route === 'PROXY'; }).length;
        var direct = groupedRecords.value.filter(function (g) { return g.route === 'DIRECT'; }).length;
        var cached = groupedRecords.value.filter(function (g) { return g.isDnsCached; }).length;
        return [
          { label: C.t('Page.Dns.Heading'), value: groupedRecords.value.length, sub: groupedRecords.value.length ? C.t('Page.Dns.Heading') : C.t('Page.Dns.Empty') },
          { label: C.t('Page.Dns.RouteProxy'), value: proxy, sub: 'GFW / Geo / Default' },
          { label: C.t('Page.Dns.RouteDirect'), value: direct, sub: C.t('Page.Dns.LegendDirect') },
          { label: C.t('Page.Dns.DnsCache'), value: cached, sub: C.t('Page.Dns.ClearSearch') }
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
          ? C.t('Page.Dns.EmptyApi')
          : C.htmlText(C.t('Page.Dns.ProxyModeNoticeHtml'));
      });

      function loadData() {
        return refreshPipeline.run(function () {
          return window.TunProxyApi.getJson('/api/status')
            .then(function (status) {
              if (status.mode !== 'tun') {
                return { status: status, records: [] };
              }

              return window.TunProxyApi.getJson('/api/dns-records').then(function (payload) {
                return { status: status, records: Array.isArray(payload) ? payload : [] };
              });
            });
        }, {
          success: function (payload) {
            if (!payload) return;
            var status = payload.status || {};
            isTunMode.value = status.mode === 'tun';
            records.value = payload.records || [];
            lastUpdate.value = C.currentTime();
          }
        });
      }

      function clearDnsCache(group) {
        if (!group || !group._records) return;
        var cached = group._records.filter(function (r) { return r.isDnsCached && r.ipAddress; });
        if (cached.length === 0) return;
        var deletes = cached.map(function (r) {
          var url = '/api/dns-cache?ip=' + encodeURIComponent(r.ipAddress);
          if (r.hostname) url += '&domain=' + encodeURIComponent(r.hostname);
          return window.TunProxyApi.delete(url);
        });
        Promise.all(deletes).then(loadData);
      }

      Vue.onMounted(function () {
        stopPolling = C.startPolling(loadData, 10000);
      });

      Vue.onBeforeUnmount(function () {
        if (stopPolling) stopPolling();
      });

      return {
        C: C,
        clearDnsCache: clearDnsCache,
        columns: columns,
        filteredRecords: filteredRecords,
        groupedRecords: groupedRecords,
        emptyMessage: emptyMessage,
        isTunMode: isTunMode,
        lastUpdate: lastUpdate,
        loading: refreshPipeline.loading,
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

        <div class="tp-dns-layout">
          <a-alert v-if="!isTunMode && !loading" type="info" show-icon>
            <template #message>{{ t('Page.Dns.Title') }}</template>
            <template #description>{{ C.htmlText(t('Page.Dns.ProxyModeNoticeHtml')) }}</template>
          </a-alert>

          <div v-if="isTunMode" class="tp-four-grid tp-dns-summary-grid">
            <a-card v-for="item in summary" :key="item.label" class="tp-summary-card" size="small">
              <div class="tp-card-line">
                <div class="tp-card-meta">
                  <div class="tp-card-label">{{ item.label }}</div>
                  <div class="tp-muted">{{ item.sub }}</div>
                </div>
                <div class="tp-card-value tp-summary-value">{{ item.value }}</div>
              </div>
            </a-card>
          </div>

          <div v-if="isTunMode" class="tp-page-grid">
          <section class="tp-section tp-dns-main">
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

            <div class="tp-dns-table-scroll tp-scrollbar">
              <a-table
                :data-source="filteredRecords"
                :columns="columns"
                :loading="loading"
                :pagination="false"
                size="small"
                :row-key="(g) => g.hostname || g._records[0].ipAddress"
                :scroll="{ x: 920 }"
              >
                <template #bodyCell="{ column, record }">
                  <template v-if="column.key === 'hostname'">
                    <strong>{{ record.hostname || '-' }}</strong>
                  </template>
                  <template v-else-if="column.key === 'ipAddresses'">
                    <span style="display:flex;flex-wrap:wrap;gap:3px">
                      <a-tag v-for="r in record._records" :key="r.ipAddress"
                             :color="r.ipAddress.startsWith('198.18.') ? undefined : r.isDnsCached ? 'blue' : 'geekblue'"
                             style="margin:0;font-family:monospace;font-size:11px">{{ r.ipAddress }}</a-tag>
                    </span>
                  </template>
                  <template v-else-if="column.key === 'route'">
                    <span class="tp-toolbar" style="justify-content:flex-start;gap:4px">
                      <a-tag :color="routeColor(record)">{{ routeLabel(record) }}</a-tag>
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
                    <a-button v-if="record.isDnsCached" danger size="small" @click="clearDnsCache(record)">{{ t('Page.Dns.ClearCache') }}</a-button>
                  </template>
                </template>
                <template #emptyText>
                  <div class="tp-empty-state">
                    <div class="tp-section-title">{{ t('Page.Dns.Empty') }}</div>
                    <div class="tp-muted">{{ emptyMessage }}</div>
                  </div>
                </template>
              </a-table>
            </div>
          </section>

          <aside>
            <section class="tp-section">
              <div class="tp-section-title">{{ t('Page.Dns.Route') }}</div>
              <div class="tp-route-map" style="margin-top: 12px">
                <div class="tp-route-node active"><strong>1. {{ t('Page.Dns.RouteStepCache') }}</strong><span class="tp-muted">{{ t('Page.Dns.PrivateIp') }} / FakeIP</span></div>
                <div class="tp-route-node"><strong>2. {{ t('Page.Dns.RouteStepDirect') }}</strong><span class="tp-muted">{{ t('Page.Dns.LegendDirect') }}</span></div>
                <div class="tp-route-node"><strong>3. {{ t('Page.Dns.RouteStepProxy') }}</strong><span class="tp-muted">{{ t('Page.Dns.LegendGfw') }} / {{ t('Page.Dns.LegendGeo') }}</span></div>
              </div>
            </section>
            <section class="tp-section">
              <div class="tp-section-title">DNS</div>
              <tp-kv-list :items="[
                { label: t('Page.Config.SystemProxyMode'), value: isTunMode ? t('Page.Config.SystemProxyMode.Tun') : t('Mode.Proxy') },
                { label: t('Page.Dns.Records'), value: C.format('Page.Dns.RecordsSummary', groupedRecords.length, records.length) },
                { label: t('Shared.RefreshAt').split('|')[0].replace('{0}', '10'), value: lastUpdate }
              ]"></tp-kv-list>
            </section>
            <section v-if="isTunMode" class="tp-section" style="margin-top:16px">
              <div class="tp-section-title" style="margin-bottom:8px">{{ t('Page.Dns.DoHNoticeTitle') }}</div>
              <div class="tp-muted" style="font-size:12px;line-height:1.6">{{ t('Page.Dns.DoHNoticeBody') }}</div>
            </section>
          </aside>
          </div>

          <section v-else-if="!loading" class="tp-section">
            <div class="tp-section-title">{{ t('Page.Dns.Heading') }}</div>
            <div class="tp-muted" style="margin-top: 8px">{{ C.htmlText(t('Page.Dns.ProxyModeNoticeHtml')) }}</div>
          </section>
        </div>
      </tp-shell>
    `
  });
})();
