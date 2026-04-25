var maxLines = 1000;
var afterId = 0;
var paused = false;
var pendingLines = [];
var filterText = '';

function levelClass(level) {
  return 'log-' + (level || 'INF');
}

function escapeHtml(text) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function setPauseButtonText() {
  var pauseButton = document.getElementById('btn-pause');
  pauseButton.textContent = paused
    ? window.TunProxyI18n.t('Page.Logs.Resume')
    : window.TunProxyI18n.t('Page.Logs.Pause');
  pauseButton.className = paused ? 'btn btn-sm btn-warning' : 'btn btn-sm btn-outline-secondary';
}

function setFilter(value) {
  document.getElementById('filter-input').value = value;
  filterText = value.toLowerCase();
  applyFilterToBox();
}

function applyFilter() {
  filterText = document.getElementById('filter-input').value.toLowerCase();
  applyFilterToBox();
}

function applyFilterToBox() {
  document.querySelectorAll('#log-box .log-line').forEach(function (line) {
    var text = (line.dataset.text || '').toLowerCase();
    line.classList.toggle('log-hidden', filterText !== '' && text.indexOf(filterText) === -1);
  });
}

function togglePause() {
  paused = !paused;
  setPauseButtonText();
  if (!paused && pendingLines.length > 0) {
    appendItems(pendingLines);
    pendingLines = [];
  }
}

function clearLogs() {
  document.getElementById('log-box').innerHTML =
    '<span id="log-empty" data-i18n-html="Page.Logs.EmptyHtml">' +
    window.TunProxyI18n.t('Page.Logs.EmptyHtml') +
    '</span>';
  pendingLines = [];
}

function buildItem(entry) {
  var text = entry.time + ' [' + entry.level + '] ' + entry.message + (entry.ex ? ' ' + entry.ex : '');
  var html = '<span class="log-time">' + escapeHtml(entry.time) + '</span>' +
    ' <span class="' + levelClass(entry.level) + '">[' + escapeHtml(entry.level) + ']</span>' +
    ' ' + escapeHtml(entry.message);

  if (entry.ex) {
    html += '<span class="log-ex">' + escapeHtml(entry.ex) + '</span>';
  }

  return {
    text: text,
    html: html
  };
}

function appendItems(items) {
  var box = document.getElementById('log-box');
  var fragment = document.createDocumentFragment();
  var empty = document.getElementById('log-empty');

  if (empty) {
    empty.style.display = 'none';
  }

  items.forEach(function (item) {
    var line = document.createElement('p');
    line.className = 'log-line mb-0';
    line.dataset.text = item.text;
    line.innerHTML = item.html;
    if (filterText && item.text.toLowerCase().indexOf(filterText) === -1) {
      line.classList.add('log-hidden');
    }
    fragment.appendChild(line);
  });

  box.appendChild(fragment);

  var lines = box.querySelectorAll('.log-line');
  while (lines.length > maxLines) {
    lines[0].remove();
    lines = box.querySelectorAll('.log-line');
  }

  if (!filterText) {
    box.scrollTop = box.scrollHeight;
  }
}

function poll() {
  window.TunProxyApi.getJson('/api/logs?after=' + afterId)
    .then(function (entries) {
      if (!entries || entries.length === 0) {
        return;
      }

      afterId = entries[entries.length - 1].id;
      document.getElementById('log-status').textContent =
        window.TunProxyI18n.format('Page.Logs.UpdatedAt', window.TunProxyI18n.timeString(new Date()));

      var items = entries.map(buildItem);
      if (paused) {
        pendingLines = pendingLines.concat(items);
      } else {
        appendItems(items);
      }
    })
    .catch(function () {
      document.getElementById('log-status').textContent = window.TunProxyI18n.t('Page.Logs.Reconnecting');
    });
}

function scrollToBottom() {
  var box = document.getElementById('log-box');
  box.scrollTop = box.scrollHeight;
  if (paused) {
    togglePause();
  }
}

document.addEventListener('tunproxy:i18n-updated', function () {
  setPauseButtonText();
  if (afterId === 0) {
    document.getElementById('log-status').textContent = window.TunProxyI18n.t('Page.Logs.Waiting');
  }
});

window.TunProxyI18n.initPage().then(function () {
  setPauseButtonText();
  document.getElementById('log-status').textContent = window.TunProxyI18n.t('Page.Logs.Waiting');

  document.getElementById('btn-pause').addEventListener('click', togglePause);
  document.getElementById('btn-clear').addEventListener('click', clearLogs);
  document.getElementById('btn-bottom').addEventListener('click', scrollToBottom);
  document.getElementById('filter-input').addEventListener('input', applyFilter);

  document.querySelectorAll('[data-filter]').forEach(function (button) {
    button.addEventListener('click', function () {
      setFilter(button.dataset.filter);
    });
  });

  setFilter('[CONN]');
  poll();
  window.setInterval(poll, 2000);
});
