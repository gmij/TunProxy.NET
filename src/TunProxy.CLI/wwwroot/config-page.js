var cfg = {};
var latestMode = null;
var ruleResourceStatus = null;
var ruleResourceFailures = {};
var upstreamProxyStatus = null;
var checkedConfigKey = null;
var savedConfigKey = null;
var proxyChecking = false;
var resourcesPreparing = false;
var savingConfig = false;

function showError(message) {
  var error = document.getElementById('alert-error');
  error.textContent = message;
  error.style.display = '';
}

function hideError() {
  document.getElementById('alert-error').style.display = 'none';
}

function buildProxyConfigFromFields() {
  return {
    host: document.getElementById('proxyHost').value.trim(),
    port: parseInt(document.getElementById('proxyPort').value, 10) || 7890,
    type: document.getElementById('proxyType').value,
    username: document.getElementById('proxyUsername').value.trim() || null,
    password: document.getElementById('proxyPassword').value || null
  };
}

function buildConfigPayloadFromFields() {
  var payload = JSON.parse(JSON.stringify(cfg));
  var systemProxyMode = getSystemProxyMode();
  payload.localProxy.listenPort = parseInt(document.getElementById('localPort').value, 10) || 8080;
  payload.localProxy.systemProxyMode = systemProxyMode;
  payload.localProxy.setSystemProxy = systemProxyMode === 'pac' || systemProxyMode === 'global';
  payload.tun.enabled = systemProxyMode === 'tun';
  payload.proxy.host = document.getElementById('proxyHost').value.trim();
  payload.proxy.port = parseInt(document.getElementById('proxyPort').value, 10) || 7890;
  payload.proxy.type = document.getElementById('proxyType').value;
  payload.proxy.username = document.getElementById('proxyUsername').value.trim() || null;
  payload.proxy.password = document.getElementById('proxyPassword').value || null;
  payload.route.mode = systemProxyMode === 'global' ? 'global' : 'smart';
  payload.route.proxyDomains = [];
  payload.route.directDomains = [];
  payload.route.enableGfwList = document.getElementById('enableGfw').checked;
  payload.route.enableGeo = document.getElementById('enableGeo').checked;
  return payload;
}

function getCurrentConfigKey() {
  return JSON.stringify(buildProxyConfigFromFields());
}

function buildSaveComparableConfig() {
  var payload = buildConfigPayloadFromFields();
  return {
    proxy: payload.proxy,
    localProxy: {
      listenPort: payload.localProxy.listenPort,
      systemProxyMode: payload.localProxy.systemProxyMode,
      setSystemProxy: payload.localProxy.setSystemProxy
    },
    tun: {
      enabled: payload.tun.enabled
    },
    route: {
      mode: payload.route.mode,
      enableGfwList: payload.route.enableGfwList,
      enableGeo: payload.route.enableGeo
    }
  };
}

function getSaveConfigKey() {
  return JSON.stringify(buildSaveComparableConfig());
}

function updateSavedConfigKeyFromFields() {
  savedConfigKey = getSaveConfigKey();
}

function hasConfigChanges() {
  return savedConfigKey !== null && getSaveConfigKey() !== savedConfigKey;
}

function canSaveConfig() {
  return hasProxyEndpoint() && hasConfigChanges();
}

function getSystemProxyMode() {
  var modeSelect = document.getElementById('systemProxyMode');
  return normalizeSystemProxyMode(modeSelect ? modeSelect.value : 'none');
}

function normalizeSystemProxyMode(value) {
  if (value === 'manual') {
    return 'global';
  }

  return value === 'pac' || value === 'global' || value === 'tun' ? value : 'none';
}

function updateModeDependentControls() {
  var localPortCol = document.getElementById('local-port-col');
  if (!localPortCol) {
    return;
  }

  var mode = getSystemProxyMode();
  localPortCol.style.display = mode === 'pac' || mode === 'global' ? '' : 'none';
}

function hasProxyEndpoint() {
  return document.getElementById('proxyHost').value.trim().length > 0
    && (parseInt(document.getElementById('proxyPort').value, 10) || 0) > 0;
}

function isProxyReadyForWorkflow() {
  return hasProxyEndpoint();
}

function isResourceEnabled(name) {
  return name === 'geo'
    ? document.getElementById('enableGeo').checked
    : document.getElementById('enableGfw').checked;
}

function isSingleRuleResourceReady(name) {
  if (!isResourceEnabled(name)) {
    return true;
  }

  var status = ruleResourceStatus ? ruleResourceStatus[name] : null;
  return !!(status && status.ready);
}

function areRuleResourcesReady() {
  return isSingleRuleResourceReady('geo') && isSingleRuleResourceReady('gfw');
}

function hasMissingRuleResources() {
  return !areRuleResourcesReady();
}

function updateWorkflowControls() {
  var proxyReady = isProxyReadyForWorkflow();
  var canPrepareResources = proxyReady && hasMissingRuleResources() && !resourcesPreparing && !savingConfig;
  var checkButton = document.getElementById('check-proxy-button');
  var saveButton = document.getElementById('save-config-button');
  var saveStatusText = document.getElementById('save-status-text');
  var geoButton = document.getElementById('geo-resource-button');
  var gfwButton = document.getElementById('gfw-resource-button');
  var prepareButton = document.getElementById('prepare-resources-button');

  checkButton.disabled = proxyChecking || resourcesPreparing || savingConfig || !hasProxyEndpoint();
  checkButton.textContent = proxyChecking
    ? window.TunProxyI18n.t('Page.Config.ProxyStatus.Checking')
    : window.TunProxyI18n.t('Page.Config.ProxyCheck');
  checkButton.className = 'btn btn-sm btn-outline-primary';

  saveButton.disabled = proxyChecking || resourcesPreparing || savingConfig || !canSaveConfig();
  saveButton.textContent = savingConfig
    ? window.TunProxyI18n.t('Page.Config.SaveStatus.Saving')
    : window.TunProxyI18n.t('Page.Config.SaveRestart');

  if (!hasProxyEndpoint()) {
    saveStatusText.textContent = window.TunProxyI18n.t('Page.Config.SaveStatus.EndpointRequired');
    saveStatusText.className = 'small text-muted';
  }
  else if (savingConfig) {
    saveStatusText.textContent = window.TunProxyI18n.t('Page.Config.SaveStatus.Saving');
    saveStatusText.className = 'small text-muted';
  }
  else if (hasConfigChanges()) {
    saveStatusText.textContent = window.TunProxyI18n.t('Page.Config.SaveStatus.Ready');
    saveStatusText.className = 'small text-success';
  }
  else {
    saveStatusText.textContent = window.TunProxyI18n.t('Page.Config.SaveStatus.NoChanges');
    saveStatusText.className = 'small text-muted';
  }

  geoButton.disabled = !proxyReady || resourcesPreparing || savingConfig;
  gfwButton.disabled = !proxyReady || resourcesPreparing || savingConfig;

  prepareButton.style.display = proxyReady && hasMissingRuleResources() ? '' : 'none';
  prepareButton.disabled = !canPrepareResources;
  prepareButton.textContent = resourcesPreparing
    ? window.TunProxyI18n.t('Page.Config.RuleResource.Downloading')
    : window.TunProxyI18n.t('Page.Config.RuleResource.Download') + ' GEO/GFW';

  var pacSection = document.getElementById('pac-section');
  if (pacSection) {
    pacSection.style.display = hasProxyEndpoint() && getSystemProxyMode() === 'pac' ? '' : 'none';
  }

  updateModeDependentControls();
}

function resetProxyStatus() {
  upstreamProxyStatus = null;
  checkedConfigKey = null;
  var badge = document.getElementById('proxy-status-badge');
  badge.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Unknown');
  badge.className = 'badge bg-secondary';
  document.getElementById('proxy-status-text').textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Hint');
  document.getElementById('proxy-check-panel').style.display = 'none';
  renderRuleResourceStatus();
  updateWorkflowControls();
}

function renderProxyStatus(status) {
  upstreamProxyStatus = status;
  var badge = document.getElementById('proxy-status-badge');
  var text = document.getElementById('proxy-status-text');
  var panel = document.getElementById('proxy-check-panel');
  var targets = document.getElementById('proxy-check-targets');

  targets.innerHTML = '';
  (status.targets || []).forEach(function (target) {
    var row = document.createElement('div');
    row.className = 'd-flex align-items-center gap-2';
    var targetBadge = document.createElement('span');
    targetBadge.className = target.isOk ? 'badge bg-success' : 'badge bg-danger';
    targetBadge.textContent = target.isOk ? '200' : (target.statusCode || 'ERR');
    var name = document.createElement('span');
    name.className = 'fw-semibold';
    name.style.width = '72px';
    name.textContent = target.name;
    var detail = document.createElement('span');
    detail.className = target.isOk ? 'text-muted' : 'text-danger';
    detail.textContent = target.isOk
      ? window.TunProxyI18n.format('Page.Config.ProxyStatus.TargetOk', target.elapsedMs)
      : (target.error || window.TunProxyI18n.t('Page.Config.ProxyStatus.TargetFailed'));
    row.appendChild(name);
    row.appendChild(targetBadge);
    row.appendChild(detail);
    targets.appendChild(row);
  });

  panel.style.display = '';
  if (status.isAvailable) {
    badge.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Available');
    badge.className = 'badge bg-success';
    text.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.AvailableHint');
    return;
  }

  badge.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Unavailable');
  badge.className = 'badge bg-danger';
  text.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.UnavailableHint');
  updateWorkflowControls();
}

function checkUpstreamProxy() {
  hideError();

  var proxyConfig = buildProxyConfigFromFields();
  var requestConfigKey = JSON.stringify(proxyConfig);
  var button = document.getElementById('check-proxy-button');
  var badge = document.getElementById('proxy-status-badge');
  upstreamProxyStatus = null;
  checkedConfigKey = null;
  proxyChecking = true;
  button.disabled = true;
  button.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Checking');
  badge.textContent = window.TunProxyI18n.t('Page.Config.ProxyStatus.Checking');
  badge.className = 'badge bg-info text-dark';
  updateWorkflowControls();

  return window.TunProxyApi.postJson('/api/upstream-proxy/check', proxyConfig)
    .then(function (status) {
      checkedConfigKey = status.isAvailable ? requestConfigKey : null;
      renderProxyStatus(status);
    })
    .catch(function (error) {
      showError(window.TunProxyI18n.format('Page.Config.ProxyStatus.CheckFailed', error.message));
      resetProxyStatus();
    })
    .finally(function () {
      proxyChecking = false;
      updateWorkflowControls();
    });
}

function renderMode(status) {
  latestMode = status.mode;
  var isTun = status.mode === 'tun';
  var badge = document.getElementById('mode-badge');

  badge.textContent = isTun
    ? window.TunProxyI18n.t('Mode.Tun')
    : window.TunProxyI18n.t('Mode.Proxy');
  badge.className = isTun ? 'badge bg-warning text-dark' : 'badge bg-info text-dark';

  document.getElementById('mode-hint-tun').style.display = isTun ? '' : 'none';
  document.getElementById('mode-hint-proxy').style.display = isTun ? 'none' : '';
  document.getElementById('local-port-row').style.display = '';
  updateModeDependentControls();
}

function loadMode() {
  return window.TunProxyApi.getJson('/api/status')
    .then(renderMode)
    .catch(function () {
      document.getElementById('mode-badge').textContent = window.TunProxyI18n.t('Page.Config.ModeUnknown');
    });
}

function loadConfig() {
  return window.TunProxyApi.getJson('/api/config')
    .then(function (config) {
      cfg = config;
      document.getElementById('localPort').value = config.localProxy.listenPort;
      document.getElementById('systemProxyMode').value = normalizeSystemProxyMode(
        config.tun.enabled ? 'tun' : (config.localProxy.systemProxyMode || (config.localProxy.setSystemProxy ? 'pac' : 'none')));
      document.getElementById('proxyHost').value = config.proxy.host;
      document.getElementById('proxyPort').value = config.proxy.port;
      document.getElementById('proxyType').value = config.proxy.type;
      document.getElementById('proxyUsername').value = config.proxy.username || '';
      document.getElementById('proxyPassword').value = config.proxy.password || '';
      document.getElementById('enableGfw').checked = config.route.enableGfwList;
      document.getElementById('enableGeo').checked = config.route.enableGeo;
      renderRuleResourceStatus();
      updateSavedConfigKeyFromFields();
      resetProxyStatus();
      updateModeDependentControls();
    });
}

function loadRuleResourceStatus() {
  return window.TunProxyApi.getJson('/api/rule-resources/status')
    .then(function (status) {
      ruleResourceStatus = status;
      renderRuleResourceStatus();
    });
}

function renderRuleResourceStatus() {
  if (!ruleResourceStatus) {
    updateWorkflowControls();
    return;
  }

  renderSingleRuleResource(
    'geo',
    ruleResourceStatus.geo,
    document.getElementById('enableGeo').checked);
  renderSingleRuleResource(
    'gfw',
    ruleResourceStatus.gfw,
    document.getElementById('enableGfw').checked);
  updateWorkflowControls();
}

function renderSingleRuleResource(name, status, enabled) {
  var row = document.getElementById(name + '-resource-row');
  var badge = document.getElementById(name + '-resource-status');
  var path = document.getElementById(name + '-resource-path');
  var button = document.getElementById(name + '-resource-button');

  row.style.opacity = enabled ? '1' : '0.55';
  path.textContent = status && status.path ? status.path : '';

  if (!enabled) {
    badge.textContent = window.TunProxyI18n.t('Page.Config.RuleResource.Disabled');
    badge.className = 'badge bg-secondary';
    button.style.display = 'none';
    return;
  }

  if (status && status.ready) {
    badge.textContent = window.TunProxyI18n.t('Page.Config.RuleResource.Ready');
    badge.className = 'badge bg-success';
    button.style.display = 'none';
    ruleResourceFailures[name] = false;
    return;
  }

  badge.textContent = window.TunProxyI18n.t('Page.Config.RuleResource.Missing');
  badge.className = 'badge bg-warning text-dark';
  button.textContent = ruleResourceFailures[name]
    ? window.TunProxyI18n.t('Page.Config.RuleResource.Retry')
    : window.TunProxyI18n.t('Page.Config.RuleResource.Download');
  button.disabled = !hasProxyEndpoint() || resourcesPreparing || savingConfig;
  button.style.display = '';
}

function downloadRuleResource(name) {
  if (!isProxyReadyForWorkflow()) {
    showError(window.TunProxyI18n.t('Page.Config.ProxyStatus.RequiredBeforeSave'));
    return;
  }

  var button = document.getElementById(name + '-resource-button');
  var badge = document.getElementById(name + '-resource-status');
  resourcesPreparing = true;
  updateWorkflowControls();
  button.disabled = true;
  button.textContent = window.TunProxyI18n.t('Page.Config.RuleResource.Downloading');
  badge.textContent = window.TunProxyI18n.t('Page.Config.RuleResource.Downloading');
  badge.className = 'badge bg-info text-dark';

  window.TunProxyApi.postJson(
    '/api/rule-resources/download?resource=' + encodeURIComponent(name),
    buildConfigPayloadFromFields())
    .then(function (status) {
      ruleResourceFailures[name] = false;
      ruleResourceStatus = status;
      renderRuleResourceStatus();
    })
    .catch(function (error) {
      ruleResourceFailures[name] = true;
      showError(window.TunProxyI18n.format('Page.Config.SaveFailed', error.message));
      renderRuleResourceStatus();
    })
    .finally(function () {
      resourcesPreparing = false;
      renderRuleResourceStatus();
    });
}

function prepareAllRuleResources() {
  hideError();

  if (!isProxyReadyForWorkflow()) {
    showError(window.TunProxyI18n.t('Page.Config.ProxyStatus.RequiredBeforeSave'));
    return Promise.resolve();
  }

  if (!hasMissingRuleResources()) {
    updateWorkflowControls();
    return Promise.resolve();
  }

  resourcesPreparing = true;
  ruleResourceFailures.geo = false;
  ruleResourceFailures.gfw = false;
  renderRuleResourceStatus();

  return window.TunProxyApi.post('/api/rule-resources/prepare', buildConfigPayloadFromFields())
    .then(loadRuleResourceStatus)
    .catch(function (error) {
      ruleResourceFailures.geo = true;
      ruleResourceFailures.gfw = true;
      showError(window.TunProxyI18n.format('Page.Config.SaveFailed', error.message));
      renderRuleResourceStatus();
    })
    .finally(function () {
      resourcesPreparing = false;
      renderRuleResourceStatus();
    });
}

function showRestartCountdown() {
  var alert = document.getElementById('alert-restart');
  var remaining = 5;

  function render() {
    alert.textContent = window.TunProxyI18n.format('Page.Config.AlertRestarting', remaining);
    alert.style.display = '';
  }

  render();
  var timer = window.setInterval(function () {
    remaining -= 1;
    if (remaining <= 0) {
      window.clearInterval(timer);
      window.location.reload();
      return;
    }

    render();
  }, 1000);
}

function saveConfig() {
  hideError();

  if (!hasProxyEndpoint()) {
    showError(window.TunProxyI18n.t('Page.Config.ProxyStatus.RequiredBeforeSave'));
    return;
  }

  if (!hasConfigChanges()) {
    updateWorkflowControls();
    return;
  }

  savingConfig = true;
  updateWorkflowControls();

  var payload = buildConfigPayloadFromFields();

  window.TunProxyApi.post('/api/config', payload)
    .then(function () {
      window.TunProxyApi.post('/api/restart').catch(function () {});
      showRestartCountdown();
    })
    .catch(function (error) {
      savingConfig = false;
      updateWorkflowControls();
      showError(window.TunProxyI18n.format('Page.Config.SaveFailed', error.message));
    });
}

function copyPacUrl() {
  var url = document.getElementById('pac-url').textContent;
  navigator.clipboard.writeText(url).then(function () {
    var pacUrl = document.getElementById('pac-url');
    pacUrl.classList.add('text-success');
    window.setTimeout(function () {
      pacUrl.classList.remove('text-success');
    }, 1500);
  });
}

function applyPac() {
  window.TunProxyApi.postJson('/api/set-pac')
    .then(function (data) {
      cfg.localProxy.systemProxyMode = 'pac';
      cfg.localProxy.setSystemProxy = true;
      cfg.tun.enabled = false;
      document.getElementById('systemProxyMode').value = 'pac';
      updateSavedConfigKeyFromFields();
      updateWorkflowControls();
      var result = document.getElementById('pac-result');
      result.textContent = window.TunProxyI18n.format('Page.Config.PacSet', data.url);
      result.className = 'small align-self-center text-success';
    })
    .catch(function () {
      var result = document.getElementById('pac-result');
      result.textContent = window.TunProxyI18n.t('Page.Config.PacSetFailed');
      result.className = 'small align-self-center text-danger';
    });
}

function clearPac() {
  window.TunProxyApi.post('/api/clear-pac')
    .then(function () {
      cfg.localProxy.systemProxyMode = 'none';
      cfg.localProxy.setSystemProxy = false;
      cfg.tun.enabled = false;
      document.getElementById('systemProxyMode').value = 'none';
      updateSavedConfigKeyFromFields();
      updateWorkflowControls();
      var result = document.getElementById('pac-result');
      result.textContent = window.TunProxyI18n.t('Page.Config.PacCleared');
      result.className = 'small align-self-center text-muted';
    })
    .catch(function () {
      var result = document.getElementById('pac-result');
      result.textContent = window.TunProxyI18n.t('Page.Config.ActionFailed');
      result.className = 'small align-self-center text-danger';
    });
}

document.addEventListener('tunproxy:i18n-updated', function () {
  if (latestMode) {
    renderMode({ mode: latestMode });
  }
  if (upstreamProxyStatus) {
    renderProxyStatus(upstreamProxyStatus);
  }
  else {
    resetProxyStatus();
  }
  renderRuleResourceStatus();
  updateWorkflowControls();
});

window.TunProxyI18n.initPage().then(function () {
  document.getElementById('check-proxy-button').addEventListener('click', checkUpstreamProxy);
  document.getElementById('save-config-button').addEventListener('click', saveConfig);
  document.getElementById('prepare-resources-button').addEventListener('click', prepareAllRuleResources);
  ['proxyHost', 'proxyPort', 'proxyType', 'proxyUsername', 'proxyPassword'].forEach(function (id) {
    document.getElementById(id).addEventListener('input', resetProxyStatus);
    document.getElementById(id).addEventListener('change', resetProxyStatus);
  });
  document.getElementById('systemProxyMode').addEventListener('change', function () {
    updateModeDependentControls();
    updateWorkflowControls();
  });
  document.getElementById('localPort').addEventListener('input', updateWorkflowControls);
  document.getElementById('localPort').addEventListener('change', updateWorkflowControls);
  document.getElementById('geo-resource-button').addEventListener('click', function () {
    downloadRuleResource('geo');
  });
  document.getElementById('gfw-resource-button').addEventListener('click', function () {
    downloadRuleResource('gfw');
  });
  document.getElementById('enableGeo').addEventListener('change', function () {
    renderRuleResourceStatus();
    updateWorkflowControls();
  });
  document.getElementById('enableGfw').addEventListener('change', function () {
    renderRuleResourceStatus();
    updateWorkflowControls();
  });
  document.getElementById('copy-pac-button').addEventListener('click', copyPacUrl);
  document.getElementById('apply-pac-button').addEventListener('click', applyPac);
  document.getElementById('clear-pac-button').addEventListener('click', clearPac);

  Promise.all([loadMode(), loadConfig(), loadRuleResourceStatus()])
    .then(updateWorkflowControls)
    .catch(function (error) {
    showError(window.TunProxyI18n.format('Page.Config.SaveFailed', error.message));
  });
});
