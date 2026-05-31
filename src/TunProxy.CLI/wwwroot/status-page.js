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
      var trafficStoreKey = 'tunproxy.status.trafficSamples.v1';
      var totalsStoreKey = 'tunproxy.status.previousTotals.v1';
      var status = Vue.ref(null);
      var refreshPipeline = C.createAsyncPipeline({ initialLoading: true });
      var serviceBusy = Vue.ref(false);
      var serviceMessage = Vue.ref('');
      var serviceAlertType = Vue.ref('info');
      var lastUpdate = Vue.ref('-');
      var previousTotals = Vue.ref(null);
      var trafficSamples = Vue.ref([]);
      var stopPolling = null;
      var sampleIntervalSeconds = 5;
      var trafficWindowSeconds = 30 * 60;
      var maxTrafficSamples = Math.floor(trafficWindowSeconds / sampleIntervalSeconds);

      function loadTrafficState() {
        try {
          var storedSamples = JSON.parse(localStorage.getItem(trafficStoreKey) || '[]');
          if (Array.isArray(storedSamples)) {
            trafficSamples.value = storedSamples
              .map(function (item) {
                return {
                  sent: Math.max(0, Number(item.sent || 0)),
                  recv: Math.max(0, Number(item.recv || 0))
                };
              })
              .slice(-maxTrafficSamples);
          }

          var storedTotals = JSON.parse(localStorage.getItem(totalsStoreKey) || 'null');
          if (storedTotals && typeof storedTotals.sent === 'number' && typeof storedTotals.recv === 'number') {
            previousTotals.value = {
              sent: Math.max(0, Number(storedTotals.sent || 0)),
              recv: Math.max(0, Number(storedTotals.recv || 0))
            };
          }
        }
        catch {
          trafficSamples.value = [];
          previousTotals.value = null;
        }
      }

      function persistTrafficState() {
        try {
          localStorage.setItem(trafficStoreKey, JSON.stringify(trafficSamples.value.slice(-maxTrafficSamples)));
          localStorage.setItem(totalsStoreKey, JSON.stringify(previousTotals.value));
        }
        catch {
        }
      }

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
        if (status.value.isDownloading) tags.push({ color: 'warning', text: C.t('Page.Status.Badge.Downloading') });
        return tags;
      });

      var overviewCards = Vue.computed(function () {
        if (!status.value) return [];
        var connectIssueText = connectIssue.value ? connectIssue.value.reason : C.t('Shared.None');
        return [
          {
            label: C.t('Page.Status.ProxyServer'),
            value: status.value.proxyHost + ':' + status.value.proxyPort,
            sub: C.t('Page.Config.ProxyType') + ': ' + (status.value.proxyType || '-')
          },
          {
            label: C.t('Page.Status.ActiveConnections'),
            value: status.value.activeConnections,
            sub: C.t('Page.Status.TotalConnections') + ': ' + (metrics.value.totalConnections || 0)
          },
          {
            label: C.t('Page.Status.Uptime'),
            value: C.fmtUptime(metrics.value.uptimeSeconds),
            sub: C.t('Shared.RefreshAt').split('|')[0].replace('{0}', '5') + ' · ' + lastUpdate.value
          },
          {
            label: C.t('Page.Config.SystemProxyMode'),
            value: status.value.mode === 'tun' ? C.t('Page.Config.SystemProxyMode.Tun') : C.t('Mode.Proxy'),
            sub: C.t('Page.Status.FakeIp') + ': ' + C.t(status.value.fakeIpMode ? 'Shared.On' : 'Shared.Off')
          },
          {
            label: C.t('Page.Status.ConnectIssue.Title'),
            value: connectIssueText,
            sub: C.t('Page.Status.FailedConnections') + ': ' + (metrics.value.failedConnections || 0)
          },
          {
            label: C.t('Page.Status.BytesSent'),
            value: C.fmtRate(metrics.value.bytesPerSecond, 'B/s'),
            sub: C.t('Page.Status.PacketRate') + ': ' + C.fmtRate(metrics.value.packetsPerSecond, 'pkt/s')
          }
        ];
      });

      var metricCards = Vue.computed(function () {
        return [
          { label: C.t('Page.Status.BytesSent'), value: C.fmtBytes(metrics.value.totalBytesSent), sub: null, icon: '↑' },
          { label: C.t('Page.Status.BytesReceived'), value: C.fmtBytes(metrics.value.totalBytesReceived), sub: null, icon: '↓' },
          { label: C.t('Page.Status.TotalConnections'), value: metrics.value.totalConnections || 0, sub: null, icon: '↔' },
          { label: C.t('Page.Status.FailedConnections'), value: metrics.value.failedConnections || 0, sub: (metrics.value.failedConnections || 0) > 0 ? C.t('Page.Status.ConnectIssue.Title') : null, icon: '!' }
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

      var trafficLineChart = Vue.computed(function () {
        var width = 1000;
        var height = 220;
        var padding = 12;
        var samples = trafficSamples.value;
        var max = Math.max.apply(null, samples.map(function (item) {
          return Math.max(Number(item.sent || 0), Number(item.recv || 0));
        }).concat([1]));

        function buildPath(key) {
          if (!samples.length) {
            return '';
          }

          var step = samples.length > 1 ? (width - padding * 2) / (samples.length - 1) : 0;
          return samples.map(function (sample, index) {
            var value = Number(sample[key] || 0);
            var x = padding + index * step;
            var y = height - padding - (value / max) * (height - padding * 2);
            return (index === 0 ? 'M' : 'L') + x.toFixed(2) + ' ' + y.toFixed(2);
          }).join(' ');
        }

        return {
          max: max,
          recvPath: buildPath('recv'),
          sentPath: buildPath('sent'),
          viewBox: '0 0 ' + width + ' ' + height
        };
      });

      var hoverInfo = Vue.ref(null);
      var chartRef = Vue.ref(null);

      function handleChartMove(event) {
        var samples = trafficSamples.value;
        if (samples.length < 2 || !chartRef.value) { hoverInfo.value = null; return; }
        var rect = chartRef.value.getBoundingClientRect();
        var relX = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
        var padding = 12;
        var step = (1000 - padding * 2) / (samples.length - 1);
        var index = Math.max(0, Math.min(samples.length - 1, Math.round((relX * 1000 - padding) / step)));
        var sample = samples[index];
        var secondsAgo = (samples.length - 1 - index) * sampleIntervalSeconds;
        var d = new Date(Date.now() - secondsAgo * 1000);
        var timeStr = ('0' + d.getHours()).slice(-2) + ':' + ('0' + d.getMinutes()).slice(-2) + ':' + ('0' + d.getSeconds()).slice(-2);
        hoverInfo.value = {
          px: event.clientX - rect.left,
          relX: relX,
          svgX: (padding + index * step).toFixed(2),
          timeStr: timeStr,
          sent: C.fmtBytes(sample.sent),
          recv: C.fmtBytes(sample.recv)
        };
      }

      function handleChartLeave() {
        hoverInfo.value = null;
      }

      function recordTrafficSample(payload) {
        var current = {
          sent: Number(payload.metrics && payload.metrics.totalBytesSent || 0),
          recv: Number(payload.metrics && payload.metrics.totalBytesReceived || 0)
        };
        var previous = previousTotals.value;
        previousTotals.value = current;
        if (!previous) {
          trafficSamples.value = [{ sent: 0, recv: 0 }];
          persistTrafficState();
          return;
        }
        trafficSamples.value = trafficSamples.value.concat({
          sent: Math.max(0, current.sent - previous.sent),
          recv: Math.max(0, current.recv - previous.recv)
        }).slice(-maxTrafficSamples);
        persistTrafficState();
      }

      function refreshStatus() {
        return refreshPipeline.run(function () {
          return window.TunProxyApi.getJson('/api/status');
        }, {
          success: function (payload) {
            if (!payload) return;
            recordTrafficSample(payload);
            status.value = payload;
            serviceBusy.value = false;
            serviceMessage.value = '';
            lastUpdate.value = C.currentTime();
          },
          error: function () {
            status.value = null;
            serviceBusy.value = false;
            serviceAlertType.value = 'warning';
            serviceMessage.value = C.t('Page.Status.ServiceUnavailableHint');
          }
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
        loadTrafficState();
        stopPolling = C.startPolling(refreshStatus, 5000);
      });

      Vue.onBeforeUnmount(function () {
        if (stopPolling) stopPolling();
      });

      return {
        trafficLineChart: trafficLineChart,
        sampleIntervalSeconds: sampleIntervalSeconds,
        trafficWindowSeconds: trafficWindowSeconds,
        connectIssue: connectIssue,
        controlService: controlService,
        diagnostics: diagnostics,
        isRunning: isRunning,
        lastUpdate: lastUpdate,
        loading: refreshPipeline.loading,
        metricCards: metricCards,
        metrics: metrics,
        modeTags: modeTags,
        overviewCards: overviewCards,
        serviceAlertType: serviceAlertType,
        serviceBusy: serviceBusy,
        serviceMessage: serviceMessage,
        status: status,
        trafficSamples: trafficSamples,
        hoverInfo: hoverInfo,
        chartRef: chartRef,
        handleChartMove: handleChartMove,
        handleChartLeave: handleChartLeave,
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
            <section class="tp-section tp-status-header">
              <div class="tp-status-primary">
                <div class="tp-pulse" :class="{ stopped: !isRunning }"></div>
                <div>
                  <div class="tp-status-title-row">
                    <div class="tp-status-title">{{ t('Page.Status.Heading') }}</div>
                    <a-tag v-for="tag in modeTags" :key="tag.text" :color="tag.color">{{ tag.text }}</a-tag>
                  </div>
                  <div class="tp-muted">{{ status && status.mode === 'tun' ? t('Page.Status.ModeTunDescription') : t('Mode.Proxy') }}</div>
                </div>
              </div>
            </section>

            <div class="tp-four-grid tp-status-overview-grid" style="margin-top: 12px">
              <a-card v-for="card in overviewCards" :key="card.label" class="tp-metric-card tp-overview-card" size="small">
                <div class="tp-card-line">
                  <div class="tp-card-meta">
                    <div class="tp-card-label">{{ card.label }}</div>
                    <div class="tp-muted">{{ card.sub }}</div>
                  </div>
                  <div class="tp-card-value tp-overview-value">{{ card.value }}</div>
                </div>
              </a-card>
            </div>

            <div class="tp-page-grid" :class="{ 'tp-page-grid-single': !(status && status.mode === 'tun') }" style="margin-top: 18px">
              <div>
                <section class="tp-section">
                  <div class="tp-section-head">
                    <div><div class="tp-section-title">{{ t('Page.Status.Traffic') }}</div><div class="tp-muted">{{ t('Page.Status.BytesSent') }} / {{ t('Page.Status.BytesReceived') }}</div></div>
                    <a-tag color="processing">{{ t('Page.Status.RollingWindow') }}</a-tag>
                  </div>
                  <div class="tp-four-grid tp-status-metric-grid">
                    <a-card v-for="card in metricCards" :key="card.label" class="tp-metric-card" size="small">
                      <div class="tp-card-line">
                        <div class="tp-card-meta">
                          <div class="tp-metric-label"><span>{{ card.label }}</span><span class="tp-metric-icon">{{ card.icon }}</span></div>
                          <div v-if="card.sub" class="tp-muted">{{ card.sub }}</div>
                        </div>
                        <div class="tp-card-value tp-metric-value">{{ card.value }}</div>
                      </div>
                    </a-card>
                  </div>
                  <div class="tp-chart">
                    <div class="tp-section-head" style="margin-bottom: 6px"><div class="tp-section-title">{{ t('Page.Status.TrafficChartTitle') }}</div><div class="tp-muted">{{ C.format('Page.Status.TrafficChartDescription', sampleIntervalSeconds) }} · {{ C.format('Shared.RefreshAt', 5, lastUpdate) }}</div></div>
                    <div class="tp-line-chart" ref="chartRef" style="position:relative;cursor:crosshair" @mousemove="handleChartMove" @mouseleave="handleChartLeave">
                      <svg preserveAspectRatio="none" :viewBox="trafficLineChart.viewBox" role="img" aria-label="Traffic line chart">
                        <path class="tp-line-grid" d="M12 208 L988 208"></path>
                        <path class="tp-line-grid" d="M12 12 L12 208"></path>
                        <path class="tp-line-sent" :d="trafficLineChart.sentPath"></path>
                        <path class="tp-line-recv" :d="trafficLineChart.recvPath"></path>
                        <line v-if="hoverInfo" :x1="hoverInfo.svgX" y1="12" :x2="hoverInfo.svgX" y2="208" class="tp-line-crosshair"></line>
                      </svg>
                      <div v-if="hoverInfo" class="tp-chart-tooltip" :style="{ left: (hoverInfo.relX > 0.75 ? hoverInfo.px - 10 : hoverInfo.px + 10) + 'px', transform: hoverInfo.relX > 0.75 ? 'translateX(-100%)' : 'none' }">
                        <div class="tp-chart-tooltip-time">{{ hoverInfo.timeStr }}</div>
                        <div><span style="color:#60a5fa">↑</span> {{ hoverInfo.sent }}</div>
                        <div><span style="color:#2dd4bf">↓</span> {{ hoverInfo.recv }}</div>
                      </div>
                    </div>
                  </div>
                </section>
              </div>
              <section class="tp-section" v-if="status && status.mode === 'tun'">
                <div class="tp-section-head"><div><div class="tp-section-title">{{ t('Page.Status.TunDiagnostics') }}</div><div class="tp-muted">{{ t('Page.Status.Wintun') }}</div></div><a-tag color="success">{{ t('Shared.Healthy') }}</a-tag></div>
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
