(function () {
  function normalizeCulture(culture) {
    return window.TunProxyI18n.normalizeCultureName(culture);
  }

  function t(key) {
    return window.TunProxyI18n.t(key);
  }

  function format(key) {
    return window.TunProxyI18n.format.apply(window.TunProxyI18n, arguments);
  }

  function fmtBytes(bytes) {
    var value = Number(bytes || 0);
    if (value >= 1e9) return (value / 1e9).toFixed(2) + ' GB';
    if (value >= 1e6) return (value / 1e6).toFixed(2) + ' MB';
    if (value >= 1e3) return (value / 1e3).toFixed(2) + ' KB';
    return value + ' B';
  }

  function fmtRate(value, suffix) {
    var numeric = Number(value || 0);
    return numeric.toFixed(numeric >= 10 ? 1 : 2) + ' ' + suffix;
  }

  function fmtUptime(seconds) {
    var total = Number(seconds || 0);
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var remaining = total % 60;
    if (hours > 0) return hours + 'h ' + minutes + 'm';
    if (minutes > 0) return minutes + 'm ' + remaining + 's';
    return remaining + 's';
  }

  function timeString(value) {
    return window.TunProxyI18n.timeString(value instanceof Date ? value : new Date(value));
  }

  function currentTime() {
    return timeString(new Date());
  }

  function htmlText(value) {
    var node = document.createElement('div');
    node.innerHTML = value || '';
    return node.textContent || node.innerText || '';
  }

  function modeLabel(mode) {
    return mode === 'tun' ? t('Mode.Tun') : t('Mode.Proxy');
  }

  function systemProxyModeLabel(mode) {
    var key = 'Page.Config.SystemProxyMode.' +
      (mode === 'pac' ? 'Pac' : mode === 'global' ? 'Global' : mode === 'tun' ? 'Tun' : 'None');
    return t(key);
  }

  function routeLabel(record) {
    if (record.route === 'DIRECT') return t('Page.Dns.RouteDirect');
    if (record.route === 'PROXY') return t('Page.Dns.RouteProxy');
    return t('Page.Dns.RouteUnknown');
  }

  function routeColor(record) {
    if (record.route === 'DIRECT') return 'default';
    if (record.reason === 'GFW') return 'success';
    if (record.reason && record.reason.indexOf('Geo:') === 0) return 'cyan';
    if (record.reason === 'Default' || record.reason === 'GeoUnknown') return 'warning';
    if (record.route === 'PROXY') return 'blue';
    return 'default';
  }

  function dateTime(value) {
    if (!value) return '-';
    var date = new Date(value);
    return Number.isNaN(date.getTime()) ? '-' : timeString(date);
  }

  function clone(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function normalizeSystemProxyMode(value) {
    if (value === 'manual') return 'global';
    return value === 'pac' || value === 'global' || value === 'tun' ? value : 'none';
  }

  function buildShellSetup(pageId, extraSetup) {
    return function () {
      var culture = Vue.ref(normalizeCulture(window.TunProxyI18n.culture));
      var activePage = Vue.ref(pageId);
      var pages = Vue.ref(window.TunProxyNav.pages.slice());

      function updateVisiblePages(mode) {
        var allPages = window.TunProxyNav.pages.slice();
        if (mode !== 'tun') {
          pages.value = allPages.filter(function (page) { return page.id !== 'dns'; });
          return;
        }
        pages.value = allPages;
      }

      var mobileOptions = Vue.computed(function () {
        return pages.value.map(function (page) {
          return {
            label: t(page.key),
            value: page.id
          };
        });
      });

      function loadModeForNavigation() {
        return window.TunProxyApi.getJson('/api/status')
          .then(function (status) {
            updateVisiblePages(status && status.mode ? status.mode : 'proxy');
          })
          .catch(function () {
            pages.value = window.TunProxyNav.pages.slice();
          });
      }

      Vue.onMounted(function () {
        loadModeForNavigation();
      });

      function setCulture(value) {
        var normalized = normalizeCulture(value);
        window.TunProxyI18n.setCulture(normalized).then(function () {
          culture.value = normalized;
          location.reload();
        });
      }

      function sidebarMeta() {
        if (pageId === 'status') return [{ label: t('Nav.Language'), value: culture.value }, { label: t('Shared.RefreshAt').split('|')[0].replace('{0}', '5') || '5s', value: '' }];
        if (pageId === 'config') return [{ label: t('Page.Config.ConfigPath').replace(/<[^>]*>/g, ''), value: '' }, { label: t('Page.Config.CurrentMode'), value: '' }];
        if (pageId === 'dns') return [{ label: t('Page.Config.SystemProxyMode.Tun'), value: '' }, { label: t('Shared.RefreshAt').split('|')[0].replace('{0}', '10') || '10s', value: '' }];
        return [{ label: t('Page.Logs.FilterConnections'), value: '[CONN]' }, { label: t('Shared.RefreshAt').split('|')[0].replace('{0}', '2') || '2s', value: '' }];
      }

      var base = {
        activePage: activePage,
        culture: culture,
        mobileOptions: mobileOptions,
        pages: pages,
        sidebarMeta: sidebarMeta(),
        setCulture: setCulture,
        t: t
      };

      return Object.assign(base, extraSetup ? extraSetup(base) : {});
    };
  }

  function mountPage(options) {
    window.TunProxyI18n.initPage().then(function () {
      var app = Vue.createApp({
        setup: buildShellSetup(options.pageId, options.setup),
        template: options.template
      });
      app.use(antd);
      registerSharedComponents(app);
      app.mount(options.el || '#app');
    });
  }

  function registerSharedComponents(app) {
    app.component('tp-shell', {
      props: {
        activePage: { type: String, required: true },
        culture: { type: String, required: true },
        eyebrow: { type: String, required: true },
        mobileOptions: { type: Array, required: true },
        pages: { type: Array, required: true },
        sidebarLines: { type: Array, default: function () { return []; } },
        title: { type: String, required: true }
      },
      emits: ['change-culture', 'update:activePage'],
      setup: function () {
        return { C: window.TunProxyConsole };
      },
      template: `
        <div :class="['tp-shell', 'tp-shell-' + activePage]">
          <aside class="tp-sidebar">
            <div class="tp-brand">
              <div class="tp-brand-mark">T</div>
              <div><div class="tp-brand-title">TunProxy</div><div class="tp-brand-subtitle">Web Console</div></div>
            </div>
            <nav class="tp-nav-list">
              <a v-for="page in pages" :key="page.id" class="tp-nav-item" :class="{ active: page.id === activePage }" :href="page.href">
                <span class="tp-nav-icon">{{ page.icon }}</span><span>{{ C.t(page.key) }}</span>
              </a>
            </nav>
            <div class="tp-sidebar-footer">
              <div v-for="line in sidebarLines" :key="line.label">{{ line.label }} <strong>{{ line.value }}</strong></div>
            </div>
          </aside>
          <main class="tp-main">
            <a-segmented class="tp-mobile-tabs" :value="activePage" :options="mobileOptions" block @change="$emit('update:activePage', $event)"></a-segmented>
            <header class="tp-topbar">
              <div><div class="tp-eyebrow">{{ eyebrow }}</div><h1 class="tp-title">{{ title }}</h1></div>
              <div class="tp-toolbar">
                <a-select :value="culture" style="width: 120px" @change="$emit('change-culture', $event)">
                  <a-select-option value="zh-CN">简体中文</a-select-option>
                  <a-select-option value="en">English</a-select-option>
                </a-select>
                <slot name="actions"></slot>
              </div>
            </header>
            <slot></slot>
          </main>
        </div>
      `
    });

    app.component('tp-kv-list', {
      props: {
        items: { type: Array, required: true }
      },
      template: `
        <div>
          <div v-for="item in items" :key="item.label" class="tp-kv-row">
            <span class="tp-muted">{{ item.label }}</span><strong>{{ item.value }}</strong>
          </div>
        </div>
      `
    });

    app.component('tp-metric-card', {
      props: {
        icon: { type: String, default: '' },
        label: { type: String, required: true },
        sub: { type: String, default: '' },
        value: { type: [String, Number], required: true }
      },
      template: `
        <div class="tp-metric-card">
          <div class="tp-metric-label"><span>{{ label }}</span><span class="tp-metric-icon">{{ icon }}</span></div>
          <div><div class="tp-metric-value">{{ value }}</div><div class="tp-muted">{{ sub }}</div></div>
        </div>
      `
    });
  }

  window.TunProxyConsole = {
    clone: clone,
    currentTime: currentTime,
    dateTime: dateTime,
    fmtBytes: fmtBytes,
    fmtRate: fmtRate,
    fmtUptime: fmtUptime,
    format: format,
    htmlText: htmlText,
    modeLabel: modeLabel,
    mountPage: mountPage,
    normalizeSystemProxyMode: normalizeSystemProxyMode,
    routeColor: routeColor,
    routeLabel: routeLabel,
    systemProxyModeLabel: systemProxyModeLabel,
    t: t,
    timeString: timeString
  };
})();
