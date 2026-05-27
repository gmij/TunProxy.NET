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

function escapeHtml(value) {
  return String(value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\"/g, '&quot;')
    .replace(/'/g, '&#39;');
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

function inferConnectIssueType(message) {
  var text = (message || '').toUpperCase();
  if (text.indexOf('PROXY_DENIED') >= 0 || text.indexOf('503') >= 0) {
    return 'proxy-denied';
  }

  if (text.indexOf('CONNECT_FAILED') >= 0 || text.indexOf('TIMEOUT') >= 0 || text.indexOf('CONNECTION REFUSED') >= 0) {
    return 'connect-failed';
  }

  if (text.indexOf('DNS') >= 0) {
    return 'dns-failed';
  }

  return 'generic';
}

function renderConnectIssue(status) {
  var alert = document.getElementById('connect-issue-alert');
  var message = status.lastTcpConnectFailure;
  if (!message) {
    alert.style.display = 'none';
    return;
  }

  var type = inferConnectIssueType(message);
  var reasonKey = 'Page.Status.ConnectIssue.Reason.Generic';
  var hintKey = 'Page.Status.ConnectIssue.Hint.Generic';
  if (type === 'proxy-denied') {
    reasonKey = 'Page.Status.ConnectIssue.Reason.ProxyDenied';
    hintKey = 'Page.Status.ConnectIssue.Hint.ProxyDenied';
  } else if (type === 'connect-failed') {
    reasonKey = 'Page.Status.ConnectIssue.Reason.ConnectFailed';
    hintKey = 'Page.Status.ConnectIssue.Hint.ConnectFailed';
  } else if (type === 'dns-failed') {
    reasonKey = 'Page.Status.ConnectIssue.Reason.DnsFailed';
    hintKey = 'Page.Status.ConnectIssue.Hint.DnsFailed';
  }

  var occurredAt = '-';
  if (status.lastTcpConnectFailureUtc) {
    occurredAt = window.TunProxyI18n.timeString(new Date(status.lastTcpConnectFailureUtc));
  }

  alert.innerHTML =
    '<div class="fw-semibold">' + window.TunProxyI18n.t('Page.Status.ConnectIssue.Title') + '</div>' +
    '<div class="mt-1"><strong>' + window.TunProxyI18n.t(reasonKey) + '</strong></div>' +
    '<div class="mt-1">' + window.TunProxyI18n.format('Page.Status.ConnectIssue.OccurredAt', occurredAt) + '</div>' +
    '<div class="mt-1 text-break">' + escapeHtml(message) + '</div>' +
    '<div class="mt-2">' + window.TunProxyI18n.t(hintKey) + '</div>';
  alert.className = 'alert alert-warning py-2 small';
  alert.style.display = '';
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
  renderConnectIssue(status);

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

    var tunWriteRetry = document.getElementById('tun-write-retry');
    tunWriteRetry.textContent = metrics.tunSendAllocationRetryAttempts || 0;
    tunWriteRetry.className = (metrics.tunSendAllocationRetryAttempts || 0) > 0 ? 'text-warning' : '';

    var tunWriteDrop = document.getElementById('tun-write-drop');
    tunWriteDrop.textContent = metrics.tunSendAllocationDrops || 0;
    tunWriteDrop.className = (metrics.tunSendAllocationDrops || 0) > 0 ? 'text-danger fw-bold' : '';
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
      document.getElementById('connect-issue-alert').style.display = 'none';
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
