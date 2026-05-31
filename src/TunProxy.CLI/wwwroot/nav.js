(function () {
  var pages = [
    { href: '/index.html', key: 'Nav.Status', icon: '●', id: 'status' },
    { href: '/config.html', key: 'Nav.Config', icon: '⚙', id: 'config' },
    { href: '/rules.html', key: 'Nav.Rules', icon: 'R', id: 'rules' },
    { href: '/dns.html', key: 'Nav.Dns', icon: 'D', id: 'dns' },
    { href: '/logs.html', key: 'Nav.Logs', icon: '≡', id: 'logs' }
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

  window.TunProxyNav = {
    pageIdFromPath: pageIdFromPath,
    pages: pages.slice()
  };
})();
