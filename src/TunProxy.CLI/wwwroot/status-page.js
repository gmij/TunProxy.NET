(function () {
  var C = window.TunProxyConsole;

  function inferConnectIssueType(message) {
    var text = (message || '').toUpperCase();
    if (text.indexOf('PROXY_DENIED') >= 0 || text.indexOf('503') >= 0) return 'proxy-denied';
    if (text.indexOf('CONNECT_FAILED') >= 0 || text.indexOf('TIMEOUT') >= 0 || text.indexOf('CONNECTION REFUSED') >= 0) return 'connect-failed';
    if (text.indexOf('DNS') >= 0) return 'dns-failed';
    return 'generic';
  }

  C.mountPage({
    pageId: 'status',
    setup: function () {
      var status = Vue.ref(null);
      var loading = Vue.ref(true);
      var serviceBusy = Vue.ref(false);
      var serviceMessage = Vue.ref('');
      var serviceAlertType = Vue.ref('info');
      var lastUpdate = Vue.ref('-');
      var previousTotals = Vue.ref(null);
      var trafficSamples = Vue.ref([]);
      var timer = null;

      var metrics = Vue.computed(function () {
        return status.value && status.value.metrics ? status.value.metrics : {};
      });

      var isRunning = Vue.computed(function () {
        return !!(status.value && status.value.isRunning);
      });

      var modeTags = Vue.computed(function () {
        if (!status.value) return [];
        var tags = [
          { color: isRunning.value ? 'success' : 'default', text: isRunning.value ? C.t('Page.Status.Badge.Running') : C.t('Page.Status.Badge.Stopped') },
          { color: status.value.mode === 'tun' ? 'blue' : 'cyan', text: C.modeLabel(status.value.mode) }
        ];
        if (status.value.mode === 'tun' && status.value.fakeIpMode) tags.push({ color: 'default', text: 'FakeIP' });
        if (status.value.isDownloading) tags.push({ color: 'warning', text: C.t('Page.Status.Badge.Downloading') });
        return tags;
      });

      var metricCards = Vue.computed(function () {
        return [
          { label: C.t('Page.Status.BytesSent'), value: C.fmtBytes(metrics.value.totalBytesSent), sub: C.t('Page.Status.BytesSent'), icon: '↑' },
          { label: C.t('Page.Status.BytesReceived'), value: C.fmtBytes(metrics.value.totalBytesReceived), sub: C.t('Page.Status.BytesReceived'), icon: '↓' },
          { label: C.t('Page.Status.TotalConnections'), value: metrics.value.totalConnections || 0, sub: C.t('Page.Status.TotalConnections'), icon: '↔' },
          { label: C.t('Page.Status.FailedConnections'), value: metrics.value.failedConnections || 0, sub: (metrics.value.failedConnections || 0) > 0 ? C.t('Page.Status.ConnectIssue.Title') : C.t('Shared.None'), icon: '!' }
        ];
      });

      var diagnostics = Vue.computed(function () {
        return [
          { name: C.t('Page.Status.RawPackets'), value: metrics.value.rawPacketsReceived || 0 },
          { name: C.t('Page.Status.Ipv6Packets'), value: metrics.value.iPv6Packets || metrics.value.ipv6Packets || 0 },
          { name: C.t('Page.Status.ParseFailures'), value: metrics.value.parseFailures || 0, color: (metrics.value.parseFailures || 0) > 0 ? 'red' : 'green' },
          { name: C.t('Page.Status.PortFiltered'), value: metrics.value.portFilteredPackets || 0 },
          { name: C.t('Page.Status.DirectRouted'), value: metrics.value.directRoutedPackets || 0 },
          { name: C.t('Page.Status.DnsQueries'), value: metrics.value.dnsQueries || 0 },
          { name: C.t('Page.Status.DnsFailures'), value: metrics.value.failedDnsQueries || 0, color: (metrics.value.failedDnsQueries || 0) > 0 ? 'orange' : 'green' },
          { name: C.t('Page.Status.TunSendAllocationRetryAttempts'), value: metrics.value.tunSendAllocationRetryAttempts || 0, color: (metrics.value.tunSendAllocationRetryAttempts || 0) > 0 ? 'orange' : 'green' },
          { name: C.t('Page.Status.TunSendAllocationDrops'), value: metrics.value.tunSendAllocationDrops || 0, color: (metrics.value.tunSendAllocationDrops || 0) > 0 ? 'red' : 'green' }
        ];
      });

      var connectIssue = Vue.computed(function () {
        if (!status.value || !status.value.lastTcpConnectFailure) return null;
        var type = inferConnectIssueType(status.value.lastTcpConnectFailure);
        var suffix = type === 'proxy-denied' ? 'ProxyDenied' : type === 'connect-failed' ? 'ConnectFailed' : type === 'dns-failed' ? 'DnsFailed' : 'Generic';
        return {
          reason: C.t('Page.Status.ConnectIssue.Reason.' + suffix),
          hint: C.t('Page.Status.ConnectIssue.Hint.' + suffix),
          message: status.value.lastTcpConnectFailure,
          time: status.value.lastTcpConnectFailureUtc ? C.timeString(status.value.lastTcpConnectFailureUtc) : '-'
        };
      });

      var bars = Vue.computed(function () {
        var values = [];
        trafficSamples.value.forEach(function (sample) {
          values.push({ type: 'sent', value: sample.sent });
          values.push({ type: 'recv', value: sample.recv });
        });
        while (values.length < 14) {
          values.unshift({ type: values.length % 2 === 0 ? 'sent' : 'recv', value: 0 });
        }
        values = values.slice(-14);
        var max = Math.max.apply(null, values.map(function (item) { return item.value; }).concat([1]));
        return values.map(function (item) {
          return {
            height: item.value > 0 ? Math.max(8, item.value / max * 100) : 3,
            type: item.type,
            value: item.value
          };
        });
      });

      function recordTrafficSample(payload) {
        var current = {
          sent: Number(payload.metrics && payload.metrics.totalBytesSent || 0),
          recv: Number(payload.metrics && payload.metrics.totalBytesReceived || 0)
        };
        var previous = previousTotals.value;
        previousTotals.value = current;
        if (!previous) {
          trafficSamples.value = [{ sent: 0, recv: 0 }];
          return;
        }
        trafficSamples.value = trafficSamples.value.concat({
          sent: Math.max(0, current.sent - previous.sent),
          recv: Math.max(0, current.recv - previous.recv)
        }).slice(-7);
      }

      function refreshStatus() {
        return window.TunProxyApi.getJson('/api/status')
          .then(function (payload) {
            recordTrafficSample(payload);
            status.value = payload;
            loading.value = false;
            serviceBusy.value = false;
            serviceMessage.value = '';
            lastUpdate.value = C.currentTime();
          })
          .catch(function () {
            loading.value = false;
            status.value = null;
            serviceBusy.value = false;
            serviceAlertType.value = 'warning';
            serviceMessage.value = C.t('Page.Status.ServiceUnavailableHint');
          });
      }

      function controlService(action) {
        serviceBusy.value = true;
        serviceAlertType.value = 'info';
        serviceMessage.value = C.t(action === 'restart' ? 'Page.Status.ServiceRestarting' : 'Page.Status.ServiceStopping');
        window.TunProxyApi.post('/api/service/' + action)
          .then(function () {
            window.setTimeout(refreshStatus, action === 'restart' ? 5000 : 1500);
          })
          .catch(function (error) {
            serviceAlertType.value = 'error';
            serviceMessage.value = C.format('Page.Status.ServiceActionFailed', error.message);
            serviceBusy.value = false;
          });
      }

      Vue.onMounted(function () {
        refreshStatus();
        timer = window.setInterval(refreshStatus, 5000);
      });

      Vue.onBeforeUnmount(function () {
        if (timer) window.clearInterval(timer);
      });

      return {
        bars: bars,
        connectIssue: connectIssue,
        controlService: controlService,
        diagnostics: diagnostics,
        isRunning: isRunning,
        lastUpdate: lastUpdate,
        loading: loading,
        metricCards: metricCards,
        metrics: metrics,
        modeTags: modeTags,
        serviceAlertType: serviceAlertType,
        serviceBusy: serviceBusy,
        serviceMessage: serviceMessage,
        status: status,
        trafficSamples: trafficSamples,
        C: C
      };
    },
    template: `
      <tp-shell
        v-model:active-page="activePage"
        :culture="culture"
        :eyebrow="t('Nav.Status') + ' / ' + t('Page.Status.Heading')"
        :mobile-options="mobileOptions"
        :pages="pages"
        :sidebar-lines="[{ label: t('Nav.Language'), value: culture }, { label: C.format('Shared.RefreshAt', 5, lastUpdate), value: '' }]"
        :title="t('Page.Status.Heading')"
        @change-culture="setCulture">
        <template #actions>
          <a-button :disabled="serviceBusy || !isRunning" @click="controlService('restart')">↻ {{ t('Page.Status.ServiceRestart') }}</a-button>
          <a-button danger :disabled="serviceBusy || !isRunning" @click="controlService('stop')">■ {{ t('Page.Status.ServiceStop') }}</a-button>
        </template>

          <a-alert v-if="serviceMessage" :type="serviceAlertType" :message="serviceMessage" show-icon style="margin-bottom: 14px"></a-alert>
          <a-alert v-if="connectIssue" type="warning" show-icon style="margin-bottom: 14px">
            <template #message>{{ t('Page.Status.ConnectIssue.Title') }}</template>
            <template #description>
              <strong>{{ connectIssue.reason }}</strong>
              <div>{{ C.format('Page.Status.ConnectIssue.OccurredAt', connectIssue.time) }}</div>
              <div class="tp-code">{{ connectIssue.message }}</div>
              <div>{{ connectIssue.hint }}</div>
            </template>
          </a-alert>

          <a-spin :spinning="loading">
            <section class="tp-status-band">
              <div class="tp-status-primary">
                <div class="tp-pulse" :class="{ stopped: !isRunning }"></div>
                <div>
                  <div class="tp-status-title-row">
                    <div class="tp-status-title">{{ isRunning ? t('Page.Status.Badge.Running') : t('Page.Status.Badge.Stopped') }}</div>
                    <a-tag v-for="tag in modeTags" :key="tag.text" :color="tag.color">{{ tag.text }}</a-tag>
                  </div>
                  <div class="tp-tag-row">
                    <a-tag color="blue">{{ status ? status.proxyType : '-' }} {{ t('Page.Config.ProxyServer') }}</a-tag>
                    <a-tag color="cyan">{{ status && status.mode === 'tun' ? '透明接管 TCP' : t('Mode.Proxy') }}</a-tag>
                    <a-tag color="success">{{ t('Page.Status.TunDiagnostics') }}</a-tag>
                  </div>
                  <div class="tp-endpoint">
                    <div class="tp-info-tile"><div class="tp-info-label">{{ t('Page.Status.ProxyServer') }}</div><div class="tp-info-value">{{ status ? status.proxyHost + ':' + status.proxyPort : '-' }}</div></div>
                    <div class="tp-info-tile"><div class="tp-info-label">{{ t('Page.Status.ActiveConnections') }}</div><div class="tp-info-value">{{ status ? status.activeConnections : '-' }}</div></div>
                    <div class="tp-info-tile"><div class="tp-info-label">{{ t('Page.Status.Uptime') }}</div><div class="tp-info-value">{{ C.fmtUptime(metrics.uptimeSeconds) }}</div></div>
                  </div>
                </div>
              </div>
              <aside class="tp-refresh-box">
                <div class="tp-section-head"><span class="tp-muted">{{ C.format('Shared.RefreshAt', 5, lastUpdate) }}</span><a-badge status="processing" :text="lastUpdate"></a-badge></div>
                <div class="tp-health-list">
                  <div class="tp-health-item"><span class="tp-muted">{{ t('Page.Status.ConnectIssue.Title') }}</span><strong>{{ connectIssue ? connectIssue.reason : t('Shared.None') }}</strong></div>
                  <div class="tp-health-item"><span class="tp-muted">{{ t('Page.Status.BytesSent') }}</span><strong>{{ C.fmtRate(metrics.bytesPerSecond, 'B/s') }}</strong></div>
                  <div class="tp-health-item"><span class="tp-muted">Packet rate</span><strong>{{ C.fmtRate(metrics.packetsPerSecond, 'pkt/s') }}</strong></div>
                  <div class="tp-health-item"><span class="tp-muted">{{ t('Page.Status.FailedConnections') }}</span><strong>{{ metrics.failedConnections || 0 }}</strong></div>
                </div>
              </aside>
            </section>

            <div class="tp-page-grid" style="margin-top: 18px">
              <div>
                <section class="tp-section">
                  <div class="tp-section-head">
                    <div><div class="tp-section-title">{{ t('Page.Status.Traffic') }}</div><div class="tp-muted">{{ t('Page.Status.BytesSent') }} / {{ t('Page.Status.BytesReceived') }}</div></div>
                    <a-tag color="processing">5s rolling</a-tag>
                  </div>
                  <div class="tp-four-grid">
                    <div v-for="card in metricCards" :key="card.label" class="tp-metric-card">
                      <div class="tp-metric-label"><span>{{ card.label }}</span><span class="tp-metric-icon">{{ card.icon }}</span></div>
                      <div><div class="tp-metric-value">{{ card.value }}</div><div class="tp-muted">{{ card.sub }}</div></div>
                    </div>
                  </div>
                  <div class="tp-chart">
                    <div class="tp-section-head" style="margin-bottom: 6px"><div class="tp-section-title">最近 7 次刷新流量变化</div><div class="tp-muted">蓝色发送，青色接收 · {{ C.format('Shared.RefreshAt', 5, lastUpdate) }}</div></div>
                    <div class="tp-bars"><div v-for="(bar, index) in bars" :key="index" class="tp-bar" :class="{ recv: bar.type === 'recv' }" :title="C.fmtBytes(bar.value)" :style="{ height: bar.height + '%' }"></div></div>
                  </div>
                </section>
                <div class="tp-two-grid" style="margin-top: 12px">
                  <div class="tp-mini-panel" style="padding: 12px"><div class="tp-muted">{{ t('Page.Status.DirectRouted') }}</div><div class="tp-section-title">{{ metrics.directRoutedPackets || 0 }}</div><div class="tp-muted">{{ t('Page.Status.PortFiltered') }} {{ metrics.portFilteredPackets || 0 }}</div></div>
                  <div class="tp-mini-panel" style="padding: 12px"><div class="tp-muted">DNS</div><div class="tp-section-title">{{ metrics.dnsQueries || 0 }} / {{ metrics.failedDnsQueries || 0 }}</div><div class="tp-muted">FakeIP {{ status && status.fakeIpMode ? 'On' : 'Off' }}</div></div>
                </div>
              </div>
              <section class="tp-section" v-if="status && status.mode === 'tun'">
                <div class="tp-section-head"><div><div class="tp-section-title">{{ t('Page.Status.TunDiagnostics') }}</div><div class="tp-muted">Wintun</div></div><a-tag color="success">正常</a-tag></div>
                <div class="tp-diagnostics">
                  <div v-for="item in diagnostics" :key="item.name" class="tp-diagnostic-row"><span class="tp-diagnostic-name">{{ item.name }}</span><span class="tp-diagnostic-value" :style="{ color: item.color === 'red' ? '#dc2626' : item.color === 'orange' ? '#d97706' : item.color === 'green' ? '#16a34a' : '#18202f' }">{{ item.value }}</span></div>
                </div>
              </section>
            </div>
          </a-spin>
      </tp-shell>
    `
  });
})();
