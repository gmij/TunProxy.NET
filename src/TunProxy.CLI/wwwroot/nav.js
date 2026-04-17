(function () {
  var pages = [
    { href: '/index.html', key: 'Nav.Status' },
    { href: '/config.html', key: 'Nav.Config' },
    { href: '/dns.html', key: 'Nav.Dns' },
    { href: '/logs.html', key: 'Nav.Logs' }
  ];

  var currentPath = location.pathname;
  if (currentPath === '/' || currentPath === '') {
    currentPath = '/index.html';
  }

  var links = pages.map(function (page) {
    var classes = 'nav-link' + (currentPath === page.href ? ' active' : '');
    return '<a class="' + classes + '" href="' + page.href + '" data-i18n="' + page.key + '"></a>';
  }).join('');

  document.body.insertAdjacentHTML(
    'afterbegin',
    '<nav class="navbar navbar-expand-sm navbar-dark bg-dark px-3 mb-4">' +
      '<a class="navbar-brand fw-bold" href="/index.html">TunProxy</a>' +
      '<div class="navbar-nav me-auto">' + links + '</div>' +
      '<div class="d-flex align-items-center gap-2 text-white small">' +
        '<label for="language-switcher" class="mb-0" data-i18n="Nav.Language"></label>' +
        '<select id="language-switcher" class="form-select form-select-sm" style="width:auto">' +
          '<option value="zh-CN">简体中文</option>' +
          '<option value="en">English</option>' +
        '</select>' +
      '</div>' +
    '</nav>'
  );
})();
