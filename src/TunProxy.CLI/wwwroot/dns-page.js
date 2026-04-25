var dnsRecords = [];
var isTunMode = false;
var sortKey = 'hostname';
var sortDirection = 'asc';

function escapeHtml(value) {
  return String(value == null ? '' : value)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

function routeLabel(record) {
  if (record.route === 'DIRECT') {
    return window.TunProxyI18n.t('Page.Dns.RouteDirect');
  }

  if (record.route === 'PROXY') {
    return window.TunProxyI18n.t('Page.Dns.RouteProxy');
  }

  return window.TunProxyI18n.t('Page.Dns.RouteUnknown');
}

function routeBadgeClass(record) {
  if (record.route === 'DIRECT') {
    return 'bg-secondary';
  }

  if (record.reason === 'GFW') {
    return 'bg-success';
  }

  if (record.reason && record.reason.indexOf('Geo:') === 0) {
    return 'bg-info text-dark';
  }

  if (record.reason === 'Default' || record.reason === 'GeoUnknown') {
    return 'bg-warning text-dark';
  }

  if (record.route === 'PROXY') {
    return 'bg-primary';
  }

  return 'bg-light text-dark';
}

function rowClass(record) {
  if (record.route === 'DIRECT') {
    return 'table-secondary';
  }

  if (record.reason === 'GFW') {
    return 'table-success';
  }

  if (record.reason && record.reason.indexOf('Geo:') === 0) {
    return 'table-info';
  }

  if (record.reason === 'Default' || record.reason === 'GeoUnknown') {
    return 'table-warning';
  }

  return '';
}

function renderRouteBadges(record) {
  var html = '<span class="badge ' + routeBadgeClass(record) + '">' + escapeHtml(routeLabel(record)) + '</span>';

  if (record.isDnsCached) {
    html += ' <span class="badge bg-primary">DNS cache</span>';
  }

  if (record.isPrivateIp) {
    html += ' <span class="badge bg-light text-dark border">' + escapeHtml(window.TunProxyI18n.t('Page.Dns.PrivateIp')) + '</span>';
  }

  return html;
}

function formatDateTime(value) {
  if (!value) {
    return '-';
  }

  var date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '-';
  }

  return window.TunProxyI18n.timeString(date);
}

function clearDnsCache(record) {
  if (!record || !record.ipAddress) {
    return;
  }

  var url = '/api/dns-cache?ip=' + encodeURIComponent(record.ipAddress);
  if (record.hostname) {
    url += '&domain=' + encodeURIComponent(record.hostname);
  }

  window.TunProxyApi.delete(url)
    .then(function () { return loadData(); });
}

function sortValue(record, key) {
  if (key === 'seenCount') {
    return Number(record.seenCount || 0);
  }

  if (key === 'lastActiveUtc') {
    var time = new Date(record.lastActiveUtc || 0).getTime();
    return Number.isNaN(time) ? 0 : time;
  }

  return String(record[key] || '').toLowerCase();
}

function compareRecords(left, right) {
  var leftValue = sortValue(left, sortKey);
  var rightValue = sortValue(right, sortKey);
  var result = 0;

  if (typeof leftValue === 'number' && typeof rightValue === 'number') {
    result = leftValue === rightValue ? 0 : (leftValue < rightValue ? -1 : 1);
  } else {
    result = String(leftValue).localeCompare(String(rightValue));
  }

  if (result === 0) {
    result = String(left.hostname || '').localeCompare(String(right.hostname || '')) ||
      String(left.ipAddress || '').localeCompare(String(right.ipAddress || ''));
  }

  return sortDirection === 'desc' ? -result : result;
}

function defaultSortDirection(key) {
  return key === 'seenCount' || key === 'lastActiveUtc' ? 'desc' : 'asc';
}

function setSort(key) {
  if (sortKey === key) {
    sortDirection = sortDirection === 'asc' ? 'desc' : 'asc';
  } else {
    sortKey = key;
    sortDirection = defaultSortDirection(key);
  }

  filterDns();
}

function updateSortIndicators() {
  document.querySelectorAll('.dns-sort-button').forEach(function (button) {
    var indicator = button.querySelector('.sort-indicator');
    if (!indicator) {
      return;
    }

    indicator.textContent = button.getAttribute('data-sort-key') === sortKey
      ? (sortDirection === 'asc' ? '^' : 'v')
      : '';
  });
}

function filterDns() {
  var query = document.getElementById('search-input').value.toLowerCase();
  var tbody = document.getElementById('dns-tbody');
  tbody.innerHTML = '';

  var entries = dnsRecords.slice();
  if (query) {
    entries = entries.filter(function (record) {
      return String(record.ipAddress || '').toLowerCase().includes(query) ||
        String(record.hostname || '').toLowerCase().includes(query) ||
        String(record.seenCount || '').toLowerCase().includes(query) ||
        String(record.route || '').toLowerCase().includes(query) ||
        String(record.reason || '').toLowerCase().includes(query) ||
        String(record.lastActiveUtc || '').toLowerCase().includes(query);
    });
  }

  entries.sort(compareRecords);

  entries.forEach(function (record) {
    var row = document.createElement('tr');
    row.className = rowClass(record);
    row.innerHTML =
      '<td class="text-break">' + escapeHtml(record.hostname) + '</td>' +
      '<td class="font-monospace text-nowrap">' + escapeHtml(record.ipAddress) + '</td>' +
      '<td class="font-monospace text-nowrap">' + escapeHtml(record.seenCount || 0) + '</td>' +
      '<td class="text-nowrap">' + renderRouteBadges(record) + '</td>' +
      '<td class="font-monospace small text-break">' + escapeHtml(record.reason) + '</td>' +
      '<td class="font-monospace small text-nowrap">' + escapeHtml(formatDateTime(record.lastActiveUtc)) + '</td>' +
      '<td class="text-end">' +
        (record.isDnsCached
          ? '<button type="button" class="btn btn-sm btn-outline-danger dns-clear-button">Clear</button>'
          : '') +
      '</td>';
    var button = row.querySelector('.dns-clear-button');
    if (button) {
      button.addEventListener('click', function () {
        clearDnsCache(record);
      });
    }
    tbody.appendChild(row);
  });

  updateSortIndicators();
  document.getElementById('dns-empty').style.display = entries.length === 0 ? '' : 'none';
}

function renderPage() {
  document.getElementById('proxy-mode-notice').style.display = isTunMode ? 'none' : '';
  document.getElementById('tun-content').style.display = isTunMode ? '' : 'none';

  if (!isTunMode) {
    return;
  }

  document.getElementById('dns-count').textContent = dnsRecords.length;
  filterDns();
  document.getElementById('last-update').textContent =
    window.TunProxyI18n.format('Shared.RefreshAt', 10, window.TunProxyI18n.timeString(new Date()));
}

function loadData() {
  window.TunProxyApi.getJson('/api/status')
    .then(function (status) {
      isTunMode = status.mode === 'tun';
      if (!isTunMode) {
        renderPage();
        return;
      }

      return window.TunProxyApi.getJson('/api/dns-records')
        .then(function (payload) {
          dnsRecords = Array.isArray(payload) ? payload : [];
          renderPage();
        });
    });
}

document.addEventListener('tunproxy:i18n-updated', renderPage);

window.TunProxyI18n.initPage().then(function () {
  document.getElementById('search-input').addEventListener('input', filterDns);
  document.getElementById('clear-search-button').addEventListener('click', function () {
    document.getElementById('search-input').value = '';
    filterDns();
  });
  document.querySelectorAll('.dns-sort-button').forEach(function (button) {
    button.addEventListener('click', function () {
      setSort(button.getAttribute('data-sort-key'));
    });
  });

  loadData();
  window.setInterval(loadData, 10000);
});
