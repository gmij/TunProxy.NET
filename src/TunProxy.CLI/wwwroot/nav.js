(function () {
  var pages = [
    { href: '/index.html', key: 'Nav.Status', icon: '●', id: 'status', script: '/status-page.js', titleKey: 'Page.Status.Title' },
    { href: '/config.html', key: 'Nav.Config', icon: '⚙', id: 'config', script: '/config-page.js', titleKey: 'Page.Config.Title' },
    { href: '/rules.html', key: 'Nav.Rules', icon: 'R', id: 'rules', script: '/rules-page.js', titleKey: 'Page.Rules.Title' },
    { href: '/dns.html', key: 'Nav.Dns', icon: 'D', id: 'dns', script: '/dns-page.js', titleKey: 'Page.Dns.Title' },
    { href: '/logs.html', key: 'Nav.Logs', icon: '≡', id: 'logs', script: '/logs-page.js', titleKey: 'Page.Logs.Title' }
  ];

  function normalizePath(path) {
    return path === '/' || path === '' ? '/index.html' : path;
  }

  function pageIdFromPath(path) {
    var current = normalizePath(path || location.pathname);
    var page = pages.find(function (item) {
      return item.href === current;
    });
    return page ? page.id : 'status';
  }

  function pageById(id) {
    return pages.find(function (item) {
      return item.id === id;
    }) || pages[0];
  }

  function pageFromPath(path) {
    return pageById(pageIdFromPath(path));
  }

  window.TunProxyNav = {
    pageById: pageById,
    pageFromPath: pageFromPath,
    pageIdFromPath: pageIdFromPath,
    pages: pages.slice()
  };
})();
