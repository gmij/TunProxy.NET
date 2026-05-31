(function () {
  var C = window.TunProxyConsole;

  C.mountPage({
    pageId: 'config',
    setup: function () {
      var messageApi = window.antd && window.antd.message ? window.antd.message : null;
      var cfg = Vue.ref(null);
      var status = Vue.ref(null);
      var resources = Vue.ref(null);
      var proxyStatus = Vue.ref(null);
      var initialLoadPipeline = C.createAsyncPipeline({ initialLoading: true });
      var proxyCheckPipeline = C.createAsyncPipeline({ initialLoading: false });
      var resourcePipeline = C.createAsyncPipeline({ initialLoading: false });
      var checking = proxyCheckPipeline.busy;
      var preparing = resourcePipeline.busy;
      var saving = Vue.ref(false);
      var error = Vue.ref('');
      var restartMessage = Vue.ref('');
      var saveBaseline = Vue.ref('');
      var pacResult = Vue.ref('');
      var authVisible = Vue.ref(false);
      var pacUrl = Vue.computed(function () {
        return window.location.origin + '/proxy.pac';
      });

      var form = Vue.reactive({
        proxyHost: '',
        proxyPort: 7890,
        proxyType: 'Http',
        proxyUsername: '',
        proxyPassword: '',
        systemProxyMode: 'none',
        localPort: 8080,
        enableGfw: true,
        enableGeo: true
      });

      var currentMode = Vue.computed(function () {
        return status.value ? status.value.mode : '';
      });

      var hasProxyEndpoint = Vue.computed(function () {
        return form.proxyHost.trim().length > 0 && Number(form.proxyPort || 0) > 0;
      });

      function buildPayload() {
        var payload = C.clone(cfg.value || {});
        payload.localProxy.listenPort = Number(form.localPort || 8080);
        payload.localProxy.systemProxyMode = form.systemProxyMode;
        payload.localProxy.setSystemProxy = form.systemProxyMode === 'pac' || form.systemProxyMode === 'global';
        payload.tun.enabled = form.systemProxyMode === 'tun';
        payload.proxy.host = form.proxyHost.trim();
        payload.proxy.port = Number(form.proxyPort || 7890);
        payload.proxy.type = form.proxyType;
        payload.proxy.username = form.proxyUsername.trim() || null;
        payload.proxy.password = form.proxyPassword || null;
        payload.route.mode = form.systemProxyMode === 'global' ? 'global' : 'smart';
        payload.route.enableGfwList = form.enableGfw;
        payload.route.enableGeo = form.enableGeo;
        return payload;
      }

      function comparablePayload() {
        if (!cfg.value) return '';
        var payload = buildPayload();
        return JSON.stringify({
          proxy: payload.proxy,
          localProxy: {
            listenPort: payload.localProxy.listenPort,
            systemProxyMode: payload.localProxy.systemProxyMode,
            setSystemProxy: payload.localProxy.setSystemProxy
          },
          tun: { enabled: payload.tun.enabled },
          route: {
            mode: payload.route.mode,
            enableGfwList: payload.route.enableGfwList,
            enableGeo: payload.route.enableGeo
          }
        });
      }

      var hasChanges = Vue.computed(function () {
        return saveBaseline.value && comparablePayload() !== saveBaseline.value;
      });

      var canSave = Vue.computed(function () {
        return hasProxyEndpoint.value && hasChanges.value && !checking.value && !preparing.value && !saving.value;
      });

      var modeOptions = Vue.computed(function () {
        return [
          { value: 'pac', title: C.t('Page.Config.SystemProxyMode.Pac'), desc: 'PAC' },
          { value: 'global', title: C.t('Page.Config.SystemProxyMode.Global'), desc: C.t('Page.Config.Route.Global') },
          { value: 'tun', title: C.t('Page.Config.SystemProxyMode.Tun'), desc: C.t('Page.Config.SystemProxyMode.TunDescription') },
          { value: 'none', title: C.t('Page.Config.SystemProxyMode.None'), desc: C.t('Page.Config.SystemProxyMode.None') }
        ];
      });

      var modeSegmentOptions = Vue.computed(function () {
        return modeOptions.value.map(function (option) {
          return { label: option.title, value: option.value };
        });
      });

      function notify(type, text) {
        if (!text) return;
        if (messageApi && typeof messageApi[type] === 'function') {
          messageApi[type](text);
          return;
        }

        pacResult.value = text;
      }

      function isResourceReady(statusMap, resourceName) {
        if (!statusMap || !statusMap[resourceName]) {
          return false;
        }

        var item = statusMap[resourceName];
        return !!item.enabled && !!item.ready;
      }

      function areEnabledResourcesReady(statusMap) {
        if (!statusMap) {
          return false;
        }

        var names = ['gfw', 'geo'];
        return names.every(function (name) {
          var item = statusMap[name];
          return !item || !item.enabled || !!item.ready;
        });
      }

      var resourceRows = Vue.computed(function () {
        var data = resources.value || {};
        return [
          { name: 'gfw', title: 'GFW', enabled: form.enableGfw, status: data.gfw },
          { name: 'geo', title: 'GEO', enabled: form.enableGeo, status: data.geo }
        ].map(function (item) {
          var ready = item.enabled && item.status && item.status.ready;
          return Object.assign(item, {
            ready: ready,
            label: item.enabled ? (ready ? C.t('Page.Config.RuleResource.Ready') : C.t('Page.Config.RuleResource.Missing')) : C.t('Page.Config.RuleResource.Disabled'),
            color: item.enabled ? (ready ? 'success' : 'warning') : 'default',
            path: item.status && item.status.path ? item.status.path : ''
          });
        });
      });

      var proxyCheckAllOk = Vue.computed(function () {
        var targets = proxyStatus.value && Array.isArray(proxyStatus.value.targets) ? proxyStatus.value.targets : [];
        return targets.length >= 3 && targets.every(function (target) { return target.isOk; });
      });

      var proxyStatusTag = Vue.computed(function () {
        if (!proxyStatus.value) {
          return {
            color: 'default',
            text: C.t('Page.Config.ProxyStatus.Unknown')
          };
        }
        return {
          color: proxyStatus.value.isAvailable ? 'success' : 'error',
          text: proxyStatus.value.isAvailable ? C.t('Page.Config.ProxyStatus.Available') : C.t('Page.Config.ProxyStatus.Unavailable')
        };
      });

      var summary = Vue.computed(function () {
        return [
          { label: C.t('Page.Status.ProxyServer'), value: form.proxyHost ? form.proxyHost + ':' + form.proxyPort : '-' },
          { label: C.t('Page.Config.SystemProxyMode'), value: C.systemProxyModeLabel(form.systemProxyMode) },
          { label: C.t('Page.Config.LocalProxyPort'), value: form.localPort },
          { label: C.t('Page.Config.RouteMode'), value: form.systemProxyMode === 'global' ? 'global' : 'smart' },
          { label: C.t('Page.Status.FakeIp'), value: C.t(cfg.value && cfg.value.tun && cfg.value.tun.fakeIpMode ? 'Shared.On' : 'Shared.Off') }
        ];
      });

      function loadConfig() {
        return window.TunProxyApi.getJson('/api/config').then(function (payload) {
          cfg.value = payload;
          form.localPort = payload.localProxy.listenPort;
          form.systemProxyMode = C.normalizeSystemProxyMode(payload.tun.enabled ? 'tun' : (payload.localProxy.systemProxyMode || (payload.localProxy.setSystemProxy ? 'pac' : 'none')));
          form.proxyHost = payload.proxy.host || '';
          form.proxyPort = payload.proxy.port || 7890;
          form.proxyType = payload.proxy.type || 'Http';
          form.proxyUsername = payload.proxy.username || '';
          form.proxyPassword = payload.proxy.password || '';
          form.enableGfw = !!payload.route.enableGfwList;
          form.enableGeo = !!payload.route.enableGeo;
          Vue.nextTick(function () {
            saveBaseline.value = comparablePayload();
          });
        });
      }

      function loadMode() {
        return window.TunProxyApi.getJson('/api/status').then(function (payload) {
          status.value = payload;
        }).catch(function () {
          status.value = null;
        });
      }

      function loadResources() {
        return window.TunProxyApi.getJson('/api/rule-resources/status').then(function (payload) {
          resources.value = payload;
          return payload;
        });
      }

      function resetProxyStatus() {
        proxyStatus.value = null;
      }

      function checkProxy() {
        if (!hasProxyEndpoint.value) return;
        error.value = '';
        proxyStatus.value = null;
        return proxyCheckPipeline.run(function () {
          return window.TunProxyApi.postJson('/api/upstream-proxy/check', {
            host: form.proxyHost.trim(),
            port: Number(form.proxyPort || 7890),
            type: form.proxyType,
            username: form.proxyUsername.trim() || null,
            password: form.proxyPassword || null
          });
        }, {
          success: function (payload) {
            proxyStatus.value = payload;
            notify('success', C.t('Page.Config.ProxyStatus.CheckCompleted'));
          },
          error: function (err) {
            error.value = C.format('Page.Config.ProxyStatus.CheckFailed', err.message);
          }
        });
      }

      function downloadResource(name) {
        if (!hasProxyEndpoint.value) {
          error.value = C.t('Page.Config.ProxyStatus.RequiredBeforeSave');
          return;
        }
        var wasReady = isResourceReady(resources.value, name);
        error.value = '';
        return resourcePipeline.run(function () {
          return window.TunProxyApi.postJson('/api/rule-resources/download?resource=' + encodeURIComponent(name), buildPayload());
        }, {
          success: function (payload) {
            resources.value = payload;
            var nowReady = isResourceReady(payload, name);
            notify('success', wasReady && nowReady
              ? C.t('Page.Config.RuleResource.AlreadyLatest')
              : C.t('Page.Config.RuleResource.Refreshed'));
          },
          error: function (err) {
            error.value = C.format('Page.Config.SaveFailed', err.message);
          }
        });
      }

      function prepareResources() {
        if (!hasProxyEndpoint.value) {
          error.value = C.t('Page.Config.ProxyStatus.RequiredBeforeSave');
          return;
        }
        var wasAllReady = areEnabledResourcesReady(resources.value);
        error.value = '';
        return resourcePipeline.run(function () {
          return window.TunProxyApi.postJson('/api/rule-resources/prepare', buildPayload())
            .then(function () {
              return loadResources();
            });
        }, {
          success: function (latest) {
            notify('success', wasAllReady && areEnabledResourcesReady(latest)
              ? C.t('Page.Config.RuleResource.AlreadyLatest')
              : C.t('Page.Config.RuleResource.Refreshed'));
          },
          error: function (err) {
            error.value = C.format('Page.Config.SaveFailed', err.message);
          }
        });
      }

      function loadInitialData() {
        return initialLoadPipeline.run(function () {
          return Promise.all([loadMode(), loadConfig(), loadResources()]);
        }, {
          error: function (err) {
            error.value = C.format('Page.Config.SaveFailed', err.message);
          }
        });
      }

      function saveConfig() {
        if (!canSave.value) return;
        saving.value = true;
        error.value = '';
        window.TunProxyApi.post('/api/config', buildPayload())
          .then(function () {
            window.TunProxyApi.post('/api/restart').catch(function () {});
            var remaining = 5;
            restartMessage.value = C.format('Page.Config.AlertRestarting', remaining);
            var countdown = window.setInterval(function () {
              remaining -= 1;
              if (remaining <= 0) {
                window.clearInterval(countdown);
                window.location.reload();
                return;
              }
              restartMessage.value = C.format('Page.Config.AlertRestarting', remaining);
            }, 1000);
          })
          .catch(function (err) {
            saving.value = false;
            error.value = C.format('Page.Config.SaveFailed', err.message);
          });
      }

      function copyPacUrl() {
        var url = pacUrl.value;
        navigator.clipboard.writeText(url).then(function () {
          pacResult.value = url;
        });
      }

      function applyPac() {
        window.TunProxyApi.postJson('/api/set-pac').then(function (payload) {
          form.systemProxyMode = 'pac';
          saveBaseline.value = comparablePayload();
          pacResult.value = C.format('Page.Config.PacSet', payload.url);
        }).catch(function () {
          pacResult.value = C.t('Page.Config.PacSetFailed');
        });
      }

      function clearPac() {
        window.TunProxyApi.post('/api/clear-pac').then(function () {
          form.systemProxyMode = 'none';
          saveBaseline.value = comparablePayload();
          pacResult.value = C.t('Page.Config.PacCleared');
        }).catch(function () {
          pacResult.value = C.t('Page.Config.ActionFailed');
        });
      }

      function handlePacMenu(event) {
        var key = event && event.key;
        if (key === 'copy') {
          copyPacUrl();
        } else if (key === 'preview') {
          window.open('/proxy.pac', '_blank', 'noopener');
        } else if (key === 'clear') {
          clearPac();
        }
      }

      Vue.onMounted(function () {
        loadInitialData();
      });

      return {
        applyPac: applyPac,
        authVisible: authVisible,
        canSave: canSave,
        checkProxy: checkProxy,
        checking: checking,
        clearPac: clearPac,
        copyPacUrl: copyPacUrl,
        currentMode: currentMode,
        downloadResource: downloadResource,
        error: error,
        form: form,
        handlePacMenu: handlePacMenu,
        hasProxyEndpoint: hasProxyEndpoint,
        loading: initialLoadPipeline.loading,
        modeOptions: modeOptions,
        modeSegmentOptions: modeSegmentOptions,
        pacUrl: pacUrl,
        pacResult: pacResult,
        prepareResources: prepareResources,
        preparing: preparing,
        proxyCheckAllOk: proxyCheckAllOk,
        proxyStatus: proxyStatus,
        proxyStatusTag: proxyStatusTag,
        resetProxyStatus: resetProxyStatus,
        resourceRows: resourceRows,
        restartMessage: restartMessage,
        saveConfig: saveConfig,
        saving: saving,
        summary: summary,
        C: C
      };
    },
    template: `
      <tp-shell
        v-model:active-page="activePage"
        :culture="culture"
        :eyebrow="t('Nav.Config') + ' / ' + t('Page.Config.StepProxyMode')"
        :mobile-options="mobileOptions"
        :pages="pages"
        :sidebar-lines="[{ label: t('Page.Config.ConfigPath').replace(/<[^>]*>/g, ''), value: '' }, { label: t('Page.Config.CurrentMode'), value: C.modeLabel(currentMode) }]"
        :title="t('Page.Config.Title')"
        @change-culture="setCulture">
        <template #actions>
          <a-button type="primary" :disabled="!canSave" :loading="saving" @click="saveConfig">{{ t('Page.Config.SaveRestart') }}</a-button>
        </template>
          <a-alert v-if="restartMessage" type="warning" :message="restartMessage" show-icon style="margin-bottom: 14px"></a-alert>
          <a-alert v-if="error" type="error" :message="error" show-icon closable style="margin-bottom: 14px" @close="error = ''"></a-alert>

          <a-spin :spinning="loading">
          <div class="tp-page-grid">
            <div>
              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-step-title"><span class="tp-step-number">1</span><span>{{ t('Page.Config.StepUpstream') }}</span></div><div class="tp-muted">{{ t('Page.Config.StepUpstreamHint') }}</div></div>
                  <div class="tp-toolbar"><a-tag color="blue">{{ form.proxyType }}</a-tag><a-button shape="circle" :type="authVisible ? 'primary' : 'default'" :title="t('Page.Config.ProxyUsername') + ' / ' + t('Page.Config.ProxyPassword')" :aria-label="t('Page.Config.ProxyUsername') + ' / ' + t('Page.Config.ProxyPassword')" @click="authVisible = !authVisible"><span class="tp-lock-icon" aria-hidden="true">&#128274;</span></a-button></div>
                </div>
                <div class="tp-three-grid">
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyHost') }}</span><a-input v-model:value="form.proxyHost" @input="resetProxyStatus" placeholder="127.0.0.1"></a-input></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyPort') }}</span><a-input-number v-model:value="form.proxyPort" style="width:100%" :min="1" :max="65535"></a-input-number></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyType') }}</span><a-select v-model:value="form.proxyType" @change="resetProxyStatus"><a-select-option value="Socks5">SOCKS5</a-select-option><a-select-option value="Http">HTTP</a-select-option></a-select></label>
                </div>
                <div v-if="authVisible" class="tp-two-grid" style="margin-top: 12px">
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyUsername') }}</span><a-input v-model:value="form.proxyUsername" @input="resetProxyStatus"></a-input></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyPassword') }}</span><a-input-password v-model:value="form.proxyPassword" @input="resetProxyStatus"></a-input-password></label>
                </div>
              </section>

              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-step-title"><span class="tp-step-number">2</span><span>{{ t('Page.Config.StepRoutingResources') }}</span></div><div class="tp-muted">{{ t('Page.Config.SmartRoutingDescription') }}</div></div>
                </div>
                <div class="tp-two-grid">
                  <label class="tp-mode-option" :class="{ active: form.enableGfw }"><div class="tp-option-title"><span>{{ t('Page.Config.EnableGfw') }}</span><a-switch v-model:checked="form.enableGfw"></a-switch></div><div class="tp-muted">GFWList</div></label>
                  <label class="tp-mode-option" :class="{ active: form.enableGeo }"><div class="tp-option-title"><span>{{ t('Page.Config.EnableGeo') }}</span><a-switch v-model:checked="form.enableGeo"></a-switch></div><div class="tp-muted">GeoIP</div></label>
                </div>
              </section>

              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-step-title"><span class="tp-step-number">3</span><span>{{ t('Page.Config.StepProxyMode') }}</span></div><div class="tp-muted">{{ t('Page.Config.StepProxyModeHint') }}</div></div>
                </div>
                <div class="tp-section-title">{{ t('Page.Config.SystemProxyMode') }}</div>
                <label class="tp-field tp-local-port-field" style="margin-top: 10px">
                  <span class="tp-field-label">{{ t('Page.Config.LocalProxyPort') }}</span>
                  <a-input-number
                    v-model:value="form.localPort"
                    style="width: 190px"
                    :min="1"
                    :max="65535"
                    :disabled="!(form.systemProxyMode === 'pac' || form.systemProxyMode === 'global')"></a-input-number>
                </label>
                <div class="tp-mode-list" role="radiogroup">
                  <button v-for="option in modeOptions" :key="option.value" type="button" class="tp-mode-option tp-mode-choice" :class="{ active: form.systemProxyMode === option.value }" role="radio" :aria-checked="form.systemProxyMode === option.value" @click="form.systemProxyMode = option.value">
                    <span><strong>{{ option.title }}</strong><span class="tp-muted">{{ option.desc }}</span></span>
                    <span v-if="form.systemProxyMode === option.value" class="tp-current-dot" :title="t('Page.Config.CurrentMode')" aria-hidden="true"></span>
                  </button>
                </div>
              </section>

            </div>

            <aside>
              <section class="tp-section" :class="{ 'tp-section-success': proxyCheckAllOk }">
                <div class="tp-section-head" style="margin-bottom: 8px">
                  <div>
                    <div class="tp-title-inline"><div class="tp-section-title">{{ t('Page.Config.ProxyCheck') }}</div><a-tag :color="proxyStatusTag.color">{{ proxyStatusTag.text }}</a-tag></div>
                    <div class="tp-muted">{{ t('Page.Config.ProxyStatus.Hint') }}</div>
                  </div>
                  <a-button shape="circle" :title="t('Page.Config.ProxyCheck')" :aria-label="t('Page.Config.ProxyCheck')" :disabled="!hasProxyEndpoint || checking || saving" :loading="checking" @click="checkProxy"><span v-if="!checking" class="tp-refresh-icon" aria-hidden="true">↻</span></a-button>
                </div>
                <template v-if="proxyStatus">
                  <div class="tp-muted" style="margin-top: 8px">{{ proxyStatus.isAvailable ? t('Page.Config.ProxyStatus.AvailableHint') : t('Page.Config.ProxyStatus.UnavailableHint') }}</div>
                  <div style="display:grid;gap:9px;margin-top:12px">
                    <div v-for="target in proxyStatus.targets || []" :key="target.name" class="tp-resource-row"><span><strong>{{ target.name }}</strong> <span class="tp-muted">{{ target.isOk ? C.format('Page.Config.ProxyStatus.TargetOk', target.elapsedMs) : (target.error || t('Page.Config.ProxyStatus.TargetFailed')) }}</span></span><a-tag :color="target.isOk ? 'success' : 'error'">{{ target.isOk ? '200' : (target.statusCode || 'ERR') }}</a-tag></div>
                  </div>
                </template>
              </section>
              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-section-title">{{ t('Page.Config.StepRoutingResources') }}</div><div class="tp-muted">{{ t('Page.Config.SmartRoutingDescription') }}</div></div>
                  <a-button shape="circle" :title="t('Page.Config.RuleResource.Download')" :aria-label="t('Page.Config.RuleResource.Download')" :disabled="preparing || saving" :loading="preparing" @click="prepareResources"><span v-if="!preparing" class="tp-refresh-icon" aria-hidden="true">↻</span></a-button>
                </div>
                <div class="tp-helper-box">
                  <div v-for="item in resourceRows" :key="item.name" class="tp-resource-row">
                    <span><strong>{{ item.title }}</strong></span>
                    <span class="tp-toolbar"><a-tag :color="item.color">{{ item.label }}</a-tag><a-button v-if="item.enabled" shape="circle" size="small" :title="t('Page.Config.RuleResource.Download')" :aria-label="t('Page.Config.RuleResource.Download')" :disabled="preparing || saving" @click="downloadResource(item.name)"><span class="tp-refresh-icon" aria-hidden="true">↻</span></a-button></span>
                  </div>
                </div>
              </section>
              <section class="tp-section">
                <div class="tp-section-title">{{ t('Page.Config.Title') }}</div>
                <div v-for="item in summary" :key="item.label" class="tp-kv-row"><span class="tp-muted">{{ item.label }}</span><strong>{{ item.value }}</strong></div>
              </section>
              <section v-if="form.systemProxyMode === 'pac'" class="tp-section">
                <div class="tp-section-title">{{ t('Page.Config.PacHeading') }}</div>
                <p class="tp-muted">{{ C.htmlText(t('Page.Config.PacDescriptionHtml')) }}</p>
                <div class="tp-helper-box tp-code">{{ pacUrl }}</div>
                <div class="tp-toolbar" style="justify-content:flex-end;margin-top:12px">
                  <a-dropdown-button type="primary" @click="applyPac">
                    {{ t('Page.Config.ApplyPac') }}
                    <template #overlay>
                      <a-menu @click="handlePacMenu">
                        <a-menu-item key="copy">{{ t('Page.Config.CopyAddress') }}</a-menu-item>
                        <a-menu-item key="preview">{{ t('Page.Config.PreviewPac') }}</a-menu-item>
                        <a-menu-item key="clear" danger>{{ t('Page.Config.ClearPac') }}</a-menu-item>
                      </a-menu>
                    </template>
                  </a-dropdown-button>
                </div>
                <p v-if="pacResult" class="tp-muted">{{ pacResult }}</p>
              </section>
            </aside>
          </div>
          </a-spin>
      </tp-shell>
    `
  });
})();
