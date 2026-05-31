(function () {
  var C = window.TunProxyConsole;

  C.mountPage({
    pageId: 'rules',
    setup: function () {
      var messageApi = window.antd && window.antd.message ? window.antd.message : null;
      var cfg = Vue.ref(null);
      var status = Vue.ref(null);
      var pipeline = C.createAsyncPipeline({ initialLoading: true });
      var saving = Vue.ref(false);
      var error = Vue.ref('');
      var restartMessage = Vue.ref('');
      var saveBaseline = Vue.ref('');

      var form = Vue.reactive({
        proxyDomainsText: '',
        directDomainsText: '',
        enableDirectFailureFallback: true,
        directFailureThreshold: 3,
        directFailureWindowSeconds: 300,
        directFailureFallbackTtlSeconds: 900
      });

      var currentMode = Vue.computed(function () {
        return status.value ? status.value.mode : '';
      });

      function notify(type, text) {
        if (!text) return;
        if (messageApi && typeof messageApi[type] === 'function') {
          messageApi[type](text);
        }
      }

      function linesToList(text) {
        return String(text || '')
          .split(/\r?\n/)
          .map(function (value) { return value.trim(); })
          .filter(function (value, index, array) {
            return value.length > 0 && array.indexOf(value) === index;
          });
      }

      function listToLines(list) {
        return Array.isArray(list) ? list.join('\n') : '';
      }

      function clampInteger(value, min, max, fallback) {
        var parsed = Number(value);
        if (!Number.isFinite(parsed)) {
          return fallback;
        }

        return Math.min(max, Math.max(min, Math.round(parsed)));
      }

      function buildPayload() {
        var payload = C.clone(cfg.value || {});
        payload.route.proxyDomains = linesToList(form.proxyDomainsText);
        payload.route.directDomains = linesToList(form.directDomainsText);
        payload.route.enableDirectFailureFallback = !!form.enableDirectFailureFallback;
        payload.route.directFailureThreshold = clampInteger(form.directFailureThreshold, 1, 20, 3);
        payload.route.directFailureWindowSeconds = clampInteger(form.directFailureWindowSeconds, 30, 3600, 300);
        payload.route.directFailureFallbackTtlSeconds = clampInteger(form.directFailureFallbackTtlSeconds, 60, 86400, 900);
        return payload;
      }

      function comparablePayload() {
        if (!cfg.value) return '';
        var payload = buildPayload();
        return JSON.stringify({
          proxyDomains: payload.route.proxyDomains,
          directDomains: payload.route.directDomains,
          enableDirectFailureFallback: payload.route.enableDirectFailureFallback,
          directFailureThreshold: payload.route.directFailureThreshold,
          directFailureWindowSeconds: payload.route.directFailureWindowSeconds,
          directFailureFallbackTtlSeconds: payload.route.directFailureFallbackTtlSeconds
        });
      }

      var proxyDomainCount = Vue.computed(function () {
        return linesToList(form.proxyDomainsText).length;
      });

      var directDomainCount = Vue.computed(function () {
        return linesToList(form.directDomainsText).length;
      });

      var hasChanges = Vue.computed(function () {
        return saveBaseline.value && comparablePayload() !== saveBaseline.value;
      });

      var canSave = Vue.computed(function () {
        return !!cfg.value && hasChanges.value && !saving.value && !pipeline.busy.value;
      });

      function loadConfig() {
        return window.TunProxyApi.getJson('/api/config').then(function (payload) {
          cfg.value = payload;
          form.proxyDomainsText = listToLines(payload.route.proxyDomains);
          form.directDomainsText = listToLines(payload.route.directDomains);
          form.enableDirectFailureFallback = payload.route.enableDirectFailureFallback !== false;
          form.directFailureThreshold = payload.route.directFailureThreshold || 3;
          form.directFailureWindowSeconds = payload.route.directFailureWindowSeconds || 300;
          form.directFailureFallbackTtlSeconds = payload.route.directFailureFallbackTtlSeconds || 900;
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

      function loadInitialData() {
        return pipeline.run(function () {
          return Promise.all([loadMode(), loadConfig()]);
        }, {
          error: function (err) {
            error.value = C.format('Page.Config.SaveFailed', err.message);
          }
        });
      }

      function saveRules() {
        if (!canSave.value) return;
        saving.value = true;
        error.value = '';
        window.TunProxyApi.post('/api/config', buildPayload())
          .then(function () {
            notify('success', C.t('Page.Rules.Saved'));
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

      Vue.onMounted(function () {
        loadInitialData();
      });

      return {
        canSave: canSave,
        currentMode: currentMode,
        directDomainCount: directDomainCount,
        error: error,
        form: form,
        loading: pipeline.loading,
        proxyDomainCount: proxyDomainCount,
        restartMessage: restartMessage,
        saveRules: saveRules,
        saving: saving,
        C: C
      };
    },
    template: `
      <tp-shell
        v-model:active-page="activePage"
        :culture="culture"
        :eyebrow="t('Nav.Rules') + ' / ' + t('Page.Rules.ManualRules')"
        :mobile-options="mobileOptions"
        :pages="pages"
        :sidebar-lines="[{ label: t('Page.Config.CurrentMode').replace('：', ''), value: C.modeLabel(currentMode) }, { label: t('Page.Rules.ProxyDomainCount'), value: proxyDomainCount }, { label: t('Page.Rules.DirectDomainCount'), value: directDomainCount }]"
        :title="t('Page.Rules.Title')"
        @change-culture="setCulture">
        <template #actions>
          <a-button type="primary" :disabled="!canSave" :loading="saving" @click="saveRules">{{ t('Page.Config.SaveRestart') }}</a-button>
        </template>
        <a-alert v-if="restartMessage" type="warning" :message="restartMessage" show-icon style="margin-bottom: 14px"></a-alert>
        <a-alert v-if="error" type="error" :message="error" show-icon closable style="margin-bottom: 14px" @close="error = ''"></a-alert>

        <a-spin :spinning="loading">
          <div class="tp-page-grid">
            <div>
              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-step-title"><span class="tp-step-number">1</span><span>{{ t('Page.Rules.ManualRules') }}</span></div><div class="tp-muted">{{ t('Page.Rules.ManualRulesHint') }}</div></div>
                </div>
                <div class="tp-two-grid">
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.ProxyDomains') }}</span><a-textarea v-model:value="form.proxyDomainsText" :auto-size="{ minRows: 12, maxRows: 22 }" placeholder="example.com"></a-textarea></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.DirectDomains') }}</span><a-textarea v-model:value="form.directDomainsText" :auto-size="{ minRows: 12, maxRows: 22 }" placeholder="example.cn"></a-textarea></label>
                </div>
              </section>

              <section class="tp-section">
                <div class="tp-section-head">
                  <div><div class="tp-step-title"><span class="tp-step-number">2</span><span>{{ t('Page.Config.DirectFailureFallback') }}</span></div><div class="tp-muted">{{ t('Page.Config.DirectFailureFallbackHint') }}</div></div>
                </div>
                <label class="tp-mode-option" :class="{ active: form.enableDirectFailureFallback }">
                  <div class="tp-option-title"><span>{{ t('Page.Config.DirectFailureFallback') }}</span><a-switch v-model:checked="form.enableDirectFailureFallback"></a-switch></div>
                </label>
                <div class="tp-three-grid" style="margin-top: 12px">
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.DirectFailureThreshold') }}</span><a-input-number v-model:value="form.directFailureThreshold" style="width:100%" :min="1" :max="20" :disabled="!form.enableDirectFailureFallback"></a-input-number></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.DirectFailureWindowSeconds') }}</span><a-input-number v-model:value="form.directFailureWindowSeconds" style="width:100%" :min="30" :max="3600" :step="30" :disabled="!form.enableDirectFailureFallback"></a-input-number></label>
                  <label class="tp-field"><span class="tp-field-label">{{ t('Page.Config.DirectFailureFallbackTtlSeconds') }}</span><a-input-number v-model:value="form.directFailureFallbackTtlSeconds" style="width:100%" :min="60" :max="86400" :step="60" :disabled="!form.enableDirectFailureFallback"></a-input-number></label>
                </div>
              </section>
            </div>

            <aside>
              <section class="tp-section">
                <div class="tp-section-title">{{ t('Page.Rules.Summary') }}</div>
                <div class="tp-kv-row"><span class="tp-muted">{{ t('Page.Rules.ProxyDomainCount') }}</span><strong>{{ proxyDomainCount }}</strong></div>
                <div class="tp-kv-row"><span class="tp-muted">{{ t('Page.Rules.DirectDomainCount') }}</span><strong>{{ directDomainCount }}</strong></div>
                <div class="tp-kv-row"><span class="tp-muted">{{ t('Page.Config.DirectFailureFallback') }}</span><strong>{{ t(form.enableDirectFailureFallback ? 'Shared.On' : 'Shared.Off') }}</strong></div>
              </section>
            </aside>
          </div>
        </a-spin>
      </tp-shell>
    `
  });
})();
