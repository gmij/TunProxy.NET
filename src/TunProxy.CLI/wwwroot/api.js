(function () {
  function request(path, options) {
    return fetch(path, options || {}).then(function (response) {
      if (response.ok) {
        return response;
      }

      return response.text().then(function (text) {
        throw new Error(text || response.statusText);
      });
    });
  }

  function json(path, options) {
    return request(path, options).then(function (response) {
      return response.json();
    });
  }

  function post(path, body) {
    var options = { method: 'POST' };
    if (body !== undefined) {
      options.headers = { 'Content-Type': 'application/json' };
      options.body = JSON.stringify(body);
    }

    return request(path, options);
  }

  function postJson(path, body) {
    return post(path, body).then(function (response) {
      return response.json();
    });
  }

  function del(path) {
    return request(path, { method: 'DELETE' });
  }

  window.TunProxyApi = {
    delete: del,
    getJson: function (path) { return json(path); },
    post: post,
    postJson: postJson
  };
})();
