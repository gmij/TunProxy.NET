var latestStatus = null;
var serviceActionBusy = false;

function fmtBytes(bytes) {
  if (bytes >= 1e9) return (bytes / 1e9).toFixed(2) + ' GB';
  if (bytes >= 1e6) return (bytes / 1e6).toFixed(2) + ' MB';
  if (bytes >= 1e3) return (bytes / 1e3).toFixed(2) + ' KB';
  return bytes + ' B';
}

function fmtUptime(seconds) {
  var hours = Math.floor(seconds / 3600);
  var minutes = Math.floor((seconds % 3600) / 60);
  var remaining = seconds % 60;
  if (hours > 0) return hours + 'h ' + minutes + 'm';
  if (minutes > 0) return minutes + 'm ' + remaining + 's';
  return remaining + 's';
}

function renderStatusBadges(status) {
  var t = window.TunProxyI18n.t;
  var badges = '';

  badges += status.isRunning
    ? '<span class="badge bg-success">' + t('Page.Status.Badge.Running') + '</span>'
    : '<span class="badge bg-secondary">' + t('Page.Status.Badge.Stopped') + '</span>';

  badges += status.mode === 'tun'
    ? ' <span class="badge bg-primary">' + t('Mode.Tun') + '</span>'
    : ' <span class="badge bg-info text-dark">' + t('Mode.Proxy') + '</span>';

  if (status.mode === 'tun' && status.fakeIpMode) {
    badges += ' <span class="badge bg-secondary">FakeIP</span>';
  }

  if (status.isDownloading) {
    badges += ' <span class="badge bg-warning text-dark">' + t('Page.Status.Badge.Downloading') + '</span>';
  }

  document.getElementById('status-badges').innerHTML = badges;
}

function renderStatus(status) {
  latestStatus = status;
  serviceActionBusy = false;
  hideServiceAction();
  var metrics = status.metrics;

  renderStatusBadges(status);
  updateServiceButtons(status.isRunning);
  document.getElementById('proxy-info').innerHTML =
    status.proxyHost + ':' + status.proxyPort + ' <span class="badge bg-light text-dark">' + status.proxyType + '</span>';
  document.getElementById('active-conn').textContent = status.activeConnections;
  document.getElementById('uptime').textContent = fmtUptime(metrics.uptimeSeconds);
  document.getElementById('bytes-sent').textContent = fmtBytes(metrics.totalBytesSent);
  document.getElementById('bytes-recv').textContent = fmtBytes(metrics.totalBytesReceived);
  document.getElementById('total-conn').textContent = metrics.totalConnections;

  var failedConnections = document.getElementById('failed-conn');
  failedConnections.textContent = metrics.failedConnections;
  failedConnections.className = metrics.failedConnections > 0 ? 'fw-bold text-danger' : 'fw-bold';

  var tunDiagnostics = document.getElementById('tun-diagnostics');
  if (status.mode === 'tun') {
    tunDiagnostics.style.display = '';
    document.getElementById('raw-packets').textContent = metrics.rawPacketsReceived;
    document.getElementById('ipv6-packets').textContent = metrics.iPv6Packets || metrics.ipv6Packets || 0;

    var parseFailures = document.getElementById('parse-fail');
    parseFailures.textContent = metrics.parseFailures;
    parseFailures.className = metrics.parseFailures > 0 ? 'text-danger' : '';

    document.getElementById('port-filtered').textContent = metrics.portFilteredPackets;
    document.getElementById('direct-routed').textContent = metrics.directRoutedPackets;
    document.getElementById('dns-queries').textContent = metrics.dnsQueries;

    var dnsFailures = document.getElementById('dns-fail');
    dnsFailures.textContent = metrics.failedDnsQueries;
    dnsFailures.className = metrics.failedDnsQueries > 0 ? 'text-warning' : '';
  } else {
    tunDiagnostics.style.display = 'none';
  }

  document.getElementById('last-update').textContent =
    window.TunProxyI18n.format('Shared.RefreshAt', 5, window.TunProxyI18n.timeString(new Date()));
}

function updateServiceButtons(isRunning) {
  var restartButton = document.getElementById('restart-service-button');
  var stopButton = document.getElementById('stop-service-button');
  restartButton.textContent = window.TunProxyI18n.t('Page.Status.ServiceRestart');
  restartButton.disabled = serviceActionBusy || !isRunning;
  stopButton.disabled = serviceActionBusy || !isRunning;
}

function hideServiceAction() {
  document.getElementById('service-action-alert').style.display = 'none';
}

function showServiceAction(messageKey, className) {
  var alert = document.getElementById('service-action-alert');
  alert.textContent = window.TunProxyI18n.t(messageKey);
  alert.className = 'alert py-2 small ' + className;
  alert.style.display = '';
}

function getServiceActionMessageKey(action) {
  return action === 'restart'
    ? 'Page.Status.ServiceRestarting'
    : 'Page.Status.ServiceStopping';
}

function postServiceAction(url) {
  return window.TunProxyApi.post(url);
}

function controlService(action) {
  serviceActionBusy = true;
  updateServiceButtons(latestStatus && latestStatus.isRunning);
  showServiceAction(getServiceActionMessageKey(action), 'alert-info');

  postServiceAction('/api/service/' + action)
    .then(function (response) {
      showServiceAction(getServiceActionMessageKey(action), 'alert-warning');
      window.setTimeout(refreshStatus, action === 'restart' ? 5000 : 1500);
    })
    .catch(function (error) {
      var alert = document.getElementById('service-action-alert');
      alert.textContent = window.TunProxyI18n.format('Page.Status.ServiceActionFailed', error.message);
      alert.className = 'alert alert-danger py-2 small';
      alert.style.display = '';
      serviceActionBusy = false;
      updateServiceButtons(latestStatus && latestStatus.isRunning);
    });
}

function refreshStatus() {
  window.TunProxyApi.getJson('/api/status')
    .then(renderStatus)
    .catch(function () {
      latestStatus = null;
      serviceActionBusy = false;
      document.getElementById('status-badges').innerHTML =
        '<span class="badge bg-danger">' + window.TunProxyI18n.t('Page.Status.Badge.Unreachable') + '</span>';
      updateServiceButtons(false);
      showServiceAction('Page.Status.ServiceUnavailableHint', 'alert-warning');
    });
}

document.addEventListener('tunproxy:i18n-updated', function () {
  if (latestStatus) {
    renderStatus(latestStatus);
  }
});

window.TunProxyI18n.initPage().then(function () {
  document.getElementById('restart-service-button').addEventListener('click', function () {
    controlService('restart');
  });
  document.getElementById('stop-service-button').addEventListener('click', function () {
    controlService('stop');
  });
  refreshStatus();
  window.setInterval(refreshStatus, 5000);
});
