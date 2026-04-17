(function () {
  var storageKey = 'tunproxy.culture';
  var supportedCultures = ['en', 'zh-CN'];
  var currentCulture = 'en';
  var translations = {};

  function normalizeCultureName(culture) {
    if (!culture) {
      return 'en';
    }

    return culture.toLowerCase().indexOf('zh') === 0 ? 'zh-CN' : 'en';
  }

  function resolveInitialCulture() {
    var urlCulture = new URLSearchParams(window.location.search).get('culture');
    if (urlCulture) {
      return normalizeCultureName(urlCulture);
    }

    var storedCulture = window.localStorage.getItem(storageKey);
    if (storedCulture) {
      return normalizeCultureName(storedCulture);
    }

    return normalizeCultureName(window.navigator.language || window.navigator.userLanguage);
  }

  function text(key) {
    return translations[key] || key;
  }

  function format(key) {
    var args = Array.prototype.slice.call(arguments, 1);
    return text(key).replace(/\{(\d+)\}/g, function (_, index) {
      var value = args[Number(index)];
      return value === undefined || value === null ? '' : String(value);
    });
  }

  function applyTranslations(root) {
    var scope = root || document;

    scope.querySelectorAll('[data-i18n]').forEach(function (node) {
      node.textContent = text(node.dataset.i18n);
    });

    scope.querySelectorAll('[data-i18n-html]').forEach(function (node) {
      node.innerHTML = text(node.dataset.i18nHtml);
    });

    scope.querySelectorAll('[data-i18n-placeholder]').forEach(function (node) {
      node.placeholder = text(node.dataset.i18nPlaceholder);
    });

    scope.querySelectorAll('[data-i18n-title]').forEach(function (node) {
      node.title = text(node.dataset.i18nTitle);
    });

    var pageTitleKey = document.body.dataset.pageTitleKey;
    if (pageTitleKey) {
      document.title = 'TunProxy - ' + text(pageTitleKey);
    }

    document.documentElement.lang = currentCulture;
    document.dispatchEvent(new CustomEvent('tunproxy:i18n-updated', {
      detail: {
        culture: currentCulture
      }
    }));
  }

  function syncLanguageSwitcher() {
    var switcher = document.getElementById('language-switcher');
    if (!switcher) {
      return;
    }

    switcher.value = currentCulture;
    switcher.onchange = function () {
      setCulture(switcher.value);
    };
  }

  async function loadCatalog(culture) {
    currentCulture = normalizeCultureName(culture);

    try {
      var response = await fetch('/api/i18n?culture=' + encodeURIComponent(currentCulture));
      translations = response.ok ? await response.json() : {};
    } catch (_) {
      translations = {};
    }

    applyTranslations(document);
    syncLanguageSwitcher();
    return translations;
  }

  async function initPage() {
    var culture = resolveInitialCulture();
    window.localStorage.setItem(storageKey, culture);
    return loadCatalog(culture);
  }

  async function setCulture(culture) {
    var normalized = normalizeCultureName(culture);
    window.localStorage.setItem(storageKey, normalized);
    return loadCatalog(normalized);
  }

  function timeString(date) {
    return date.toLocaleTimeString(currentCulture);
  }

  window.TunProxyI18n = {
    format: format,
    initPage: initPage,
    loadCatalog: loadCatalog,
    normalizeCultureName: normalizeCultureName,
    setCulture: setCulture,
    supportedCultures: supportedCultures.slice(),
    t: text,
    timeString: timeString,
    get culture() {
      return currentCulture;
    }
  };
})();
