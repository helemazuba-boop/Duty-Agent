
    'use strict';
    const DEFAULT_CONFIG = {
      version: 1,
      selected_plan_id: 'standard',
      plan_presets: [
        {
          id: 'standard',
          name: '标准',
          mode_id: 'standard',
          api_key: '',
          base_url: 'https://integrate.api.nvidia.com/v1',
          model: 'moonshotai/kimi-k2-thinking',
          model_profile: 'auto',
          provider_hint: '',
          multi_agent_execution_mode: 'auto'
        },
        {
          id: 'campus-6agent',
          name: '6Agent',
          mode_id: 'campus_6agent',
          api_key: '',
          base_url: 'https://integrate.api.nvidia.com/v1',
          model: 'moonshotai/kimi-k2-thinking',
          model_profile: 'auto',
          provider_hint: '',
          multi_agent_execution_mode: 'auto'
        },
        {
          id: 'incremental-small',
          name: '增量小模型',
          mode_id: 'incremental_small',
          api_key: '',
          base_url: 'https://integrate.api.nvidia.com/v1',
          model: 'moonshotai/kimi-k2-thinking',
          model_profile: 'auto',
          provider_hint: '',
          multi_agent_execution_mode: 'auto'
        }
      ],
      per_day: 2,
      duty_rule: '',
      notification_templates: ['{scene}{status}，日期：{date}，区域：{areas}']
    };

    const DEFAULT_ROSTER = [];

    const DEFAULT_STATE = { seed_anchor: '', schedule_pool: [] };
    const DAY = ['周日', '周一', '周二', '周三', '周四', '周五', '周六'];
    const MOCK_GENERATION_DAYS = 5;
    const API_KEY_MASK = '***仅运行时***';
    const LEGACY_API_KEY_MASK = '***runtime-only***';
    const HOST_API_KEY_MASK = '********';
    const urlParams = new URLSearchParams(window.location.search);
    const forceHttpTransport = urlParams.get('transport') === 'http';
    const hasBridge = !!(window.chrome && window.chrome.webview) && !forceHttpTransport;
    const hasBackendTransport = window.location.protocol.startsWith('http');

    let runtimeMode = 'mock';
    let selectedRosterId = null;
    let logFilter = 'all';
    let runInProgress = false;
    const logEntries = [];
    const LOG_MAX = 500;
    const LOG_LEVEL_LABELS = {
      INFO: '信息',
      WARN: '警告',
      ERROR: '错误',
      STREAM: '流式',
      SEND: '发送',
      RECV: '接收',
      OK: '成功'
    };
    const LOCAL_DEBUG_PREFS_STORAGE_KEY = 'duty-agent.local-debug-prefs.v1';
    const STREAM_FLUSH_INTERVAL_MS = 33;
    let logRenderScheduled = false;
    let pendingStreamText = '';
    let pendingStreamStatus = '';
    let streamFlushTimer = 0;
    let store = {
      config: normalizeConfig({ ...DEFAULT_CONFIG, ...loadLocalDebugPrefs() }),
      roster: normalizeRoster(DEFAULT_ROSTER),
      state: normalizeState(DEFAULT_STATE)
    };
    let lastLoadedBackendConfig = clone(store.config);
    let lastLoadedHostVersion = 1;
    let backendConfigDirty = false;
    let pendingRosterSavePromise = Promise.resolve(true);
    let settingsAutoSaveTimer = 0;
    let settingsSaveInFlight = false;
    let settingsSaveQueued = false;

    const $ = (id) => document.getElementById(id);

    function clone(v) { return JSON.parse(JSON.stringify(v)); }
    function createClientTraceId(prefix = 'web') {
      if (window.crypto && typeof window.crypto.randomUUID === 'function') {
        return `${prefix}-${window.crypto.randomUUID().replace(/-/g, '')}`;
      }
      return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
    }
    function int(v, d) { const n = Number(v); return Number.isFinite(n) ? Math.trunc(n) : d; }
    function bool(v, d) {
      if (typeof v === 'boolean') return v;
      if (typeof v === 'string') {
        const s = v.trim().toLowerCase();
        if (['true', '1', 'yes', 'on'].includes(s)) return true;
        if (['false', '0', 'no', 'off'].includes(s)) return false;
      }
      return d;
    }
    function clamp(n, min, max) { return Math.max(min, Math.min(max, n)); }
    function now() {
      const d = new Date();
      return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
    }
    function today() { const d = new Date(); d.setHours(0, 0, 0, 0); return d; }
    function addDays(date, n) { const d = new Date(date); d.setDate(d.getDate() + n); d.setHours(0, 0, 0, 0); return d; }
    function dateIso(date) {
      const y = date.getFullYear();
      const m = String(date.getMonth() + 1).padStart(2, '0');
      const d = String(date.getDate()).padStart(2, '0');
      return `${y}-${m}-${d}`;
    }
    function parseIso(s) {
      if (typeof s !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(s)) return null;
      const [y, m, d] = s.split('-').map(Number);
      const dt = new Date(y, m - 1, d);
      dt.setHours(0, 0, 0, 0);
      return (dt.getFullYear() === y && dt.getMonth() === m - 1 && dt.getDate() === d) ? dt : null;
    }
    function html(s) {
      return String(s)
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
    }
    function list(input, fallback, allowEmpty = false) {
      const seen = new Set();
      const out = [];
      if (Array.isArray(input)) {
        for (const raw of input) {
          const t = String(raw ?? '').trim();
          if (!t || seen.has(t)) continue;
          seen.add(t);
          out.push(t);
        }
      }
      if (out.length || allowEmpty) return out;
      return clone(fallback);
    }
    function names(v) { return list(v, [], true); }
    function normalizePlanModeId(value) {
      const normalized = String(value ?? '').trim().toLowerCase();
      if (['campus_6agent', 'campus6agent', '6agent', 'multi_agent'].includes(normalized)) return 'campus_6agent';
      if (['incremental_small', 'incremental', 'small_incremental'].includes(normalized)) return 'incremental_small';
      return 'standard';
    }
    function normalizeModelProfile(value) {
      const normalized = String(value ?? '').trim().toLowerCase();
      if (['cloud_general'].includes(normalized)) return 'cloud';
      if (['campus', 'school_small'].includes(normalized)) return 'campus_small';
      if (['edge_tuned', 'edge_finetuned'].includes(normalized)) return 'edge';
      return ['auto', 'cloud', 'campus_small', 'edge', 'custom'].includes(normalized) ? normalized : 'auto';
    }
    function normalizeMultiAgentExecutionMode(value) {
      const normalized = String(value ?? '').trim().toLowerCase();
      if (['parallel', 'concurrent'].includes(normalized)) return 'parallel';
      if (['serial', 'sequential'].includes(normalized)) return 'serial';
      return 'auto';
    }
    function normalizePlanPresets(input) {
      const source = Array.isArray(input) ? input : [];
      const fallback = clone(DEFAULT_CONFIG.plan_presets);
      const items = source.length ? source : fallback;
      const out = [];
      const usedIds = new Set();
      for (let index = 0; index < items.length; index += 1) {
        const raw = items[index] && typeof items[index] === 'object' ? items[index] : {};
        const modeId = normalizePlanModeId(raw.mode_id);
        let id = String(raw.id ?? '').trim() || (modeId === 'campus_6agent' ? 'campus-6agent' : modeId === 'incremental_small' ? 'incremental-small' : 'standard');
        id = id.toLowerCase().replace(/[^a-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '') || `plan-${index + 1}`;
        let uniqueId = id;
        let suffix = 2;
        while (usedIds.has(uniqueId)) {
          uniqueId = `${id}-${suffix}`;
          suffix += 1;
        }
        usedIds.add(uniqueId);
        out.push({
          id: uniqueId,
          name: String(raw.name ?? '').trim() || (modeId === 'campus_6agent' ? '6Agent' : modeId === 'incremental_small' ? '增量小模型' : '标准'),
          mode_id: modeId,
          api_key: String(raw.api_key ?? ''),
          base_url: String(raw.base_url ?? DEFAULT_CONFIG.plan_presets[0].base_url).trim() || DEFAULT_CONFIG.plan_presets[0].base_url,
          model: String(raw.model ?? DEFAULT_CONFIG.plan_presets[0].model).trim() || DEFAULT_CONFIG.plan_presets[0].model,
          model_profile: normalizeModelProfile(raw.model_profile),
          provider_hint: String(raw.provider_hint ?? '').trim(),
          multi_agent_execution_mode: modeId === 'campus_6agent'
            ? normalizeMultiAgentExecutionMode(raw.multi_agent_execution_mode)
            : 'auto'
        });
      }
      return out.length ? out : fallback;
    }
    function normalizeSelectedPlanId(value, planPresets) {
      const plans = normalizePlanPresets(planPresets);
      const raw = String(value ?? '').trim();
      if (raw && plans.some((plan) => plan.id === raw)) return raw;
      const normalizedMode = normalizePlanModeId(raw);
      const modeMatch = plans.find((plan) => plan.mode_id === normalizedMode);
      return modeMatch ? modeMatch.id : plans[0].id;
    }
    function getSelectedPlan(config) {
      const plans = normalizePlanPresets(config?.plan_presets);
      const selectedPlanId = normalizeSelectedPlanId(config?.selected_plan_id, plans);
      return plans.find((plan) => plan.id === selectedPlanId) || plans[0];
    }
    function applyHostTheme(payload) {
      if (!payload || typeof payload !== 'object') return;
      const root = document.documentElement;
      const map = {
        '--bg1': payload.bg1,
        '--bg2': payload.bg2,
        '--card': payload.card,
        '--line': payload.line,
        '--text': payload.text,
        '--muted': payload.muted,
        '--shadow': payload.shadow,
        '--input-bg': payload.input_bg,
        '--table-head': payload.table_head
      };
      for (const [key, value] of Object.entries(map)) {
        if (typeof value === 'string' && value.trim()) {
          root.style.setProperty(key, value.trim());
        }
      }
      root.setAttribute('data-host-theme', String(payload.mode || 'light'));
    }



    function normalizeConfig(input) {
      const raw = input && typeof input === 'object' ? input : {};
      const perDay = clamp(int(raw.per_day, DEFAULT_CONFIG.per_day), 1, 30);
      const planPresets = normalizePlanPresets(raw.plan_presets);
      return {
        version: Math.max(1, int(raw.version, DEFAULT_CONFIG.version)),
        selected_plan_id: normalizeSelectedPlanId(raw.selected_plan_id, planPresets),
        plan_presets: planPresets,
        per_day: perDay,
        duty_rule: String(raw.duty_rule ?? ''),
        notification_templates: list(raw.notification_templates, DEFAULT_CONFIG.notification_templates)
      };
    }

    function buildLocalDebugPrefs(config) {
      const normalized = normalizeConfig(config);
      return {
        per_day: normalized.per_day,
        notification_templates: list(normalized.notification_templates, DEFAULT_CONFIG.notification_templates)
      };
    }

    function loadLocalDebugPrefs() {
      try {
        if (!window.localStorage) return {};
        const raw = window.localStorage.getItem(LOCAL_DEBUG_PREFS_STORAGE_KEY);
        if (!raw) return {};
        const parsed = JSON.parse(raw);
        return buildLocalDebugPrefs(parsed);
      } catch {
        return {};
      }
    }

    function persistLocalDebugPrefs(reason, options) {
      const opts = options && typeof options === 'object' ? options : {};
      try {
        if (!window.localStorage) return false;
        const payload = buildLocalDebugPrefs(store.config);
        window.localStorage.setItem(LOCAL_DEBUG_PREFS_STORAGE_KEY, JSON.stringify(payload));
        if (!opts.quiet) {
          log('INFO', '本地调试项已保存到当前设备。', {
            reason: reason || 'manual',
            per_day: payload.per_day,
            template_count: payload.notification_templates.length
          });
        }
        return true;
      } catch (e) {
        if (!opts.quiet) {
          const message = e instanceof Error ? e.message : String(e);
          log('WARN', '本地调试项保存失败。', { reason: reason || 'manual', message });
        }
        return false;
      }
    }

    function normalizeRoster(input) {
      if (!Array.isArray(input)) return [];
      const ids = new Set();
      const out = [];
      for (const r of input) {
        if (!r || typeof r !== 'object') continue;
        const id = int(r.id, NaN);
        const name = String(r.name ?? '').trim();
        if (!Number.isFinite(id) || id <= 0 || !name || ids.has(id)) continue;
        ids.add(id);
        out.push({ id, name, active: bool(r.active, true) });
      }
      out.sort((a, b) => a.id - b.id);
      return out;
    }

    function normalizeAssignments(raw) {
      const out = {};
      if (!raw || typeof raw !== 'object') return out;
      for (const [area, values] of Object.entries(raw)) {
        const key = String(area ?? '').trim();
        if (!key) continue;
        const arr = names(values);
        if (arr.length) out[key] = arr;
      }
      return out;
    }

    function normalizeItem(item) {
      if (!item || typeof item !== 'object') return null;
      const map = normalizeAssignments(item.area_assignments);
      const cls = names(item.classroom_students);
      const cln = names(item.cleaning_area_students);
      const stu = names(item.students);
      return {
        date: String(item.date ?? '').trim(),
        day: String(item.day ?? '').trim(),
        students: stu,
        classroom_students: cls,
        cleaning_area_students: cln,
        area_assignments: map
      };
    }

    function normalizeState(input) {
      const raw = input && typeof input === 'object' ? input : {};
      const state = { seed_anchor: String(raw.seed_anchor ?? ''), schedule_pool: [] };
      if (Array.isArray(raw.schedule_pool)) {
        for (const it of raw.schedule_pool) {
          const n = normalizeItem(it);
          if (n) state.schedule_pool.push(n);
        }
      }
      state.schedule_pool.sort((a, b) => a.date.localeCompare(b.date));
      return state;
    }

    async function readResponsePayload(response) {
      const text = await response.text();
      if (!text) return {};
      try {
        return JSON.parse(text);
      } catch {
        return { message: text };
      }
    }

    function responseErrorMessage(response, payload) {
      if (payload && typeof payload.detail === 'string' && payload.detail.trim()) return payload.detail.trim();
      if (payload && typeof payload.message === 'string' && payload.message.trim()) return payload.message.trim();
      return `HTTP ${response.status}`;
    }

    function assignment(item, areaOrder = []) {
      const map = normalizeAssignments(item?.area_assignments);
      if (!Object.keys(map).length) {
        const classroom = names(item?.classroom_students);
        const cleaning = names(item?.cleaning_area_students);
        const students = names(item?.students);
        if (classroom.length) map['教室'] = classroom;
        if (cleaning.length) map['清洁区'] = cleaning;
        if (!Object.keys(map).length && students.length) map.default_area = students;
      }
      const ordered = {};
      for (const area of areaOrder) if (map[area]) ordered[area] = [...map[area]];
      for (const [k, v] of Object.entries(map)) if (!ordered[k]) ordered[k] = [...v];
      return ordered;
    }

    function isSecretKeyName(key) {
      const k = String(key || '').trim().toLowerCase();
      return k === 'api_key' ||
        k === 'apikey' ||
        k === 'encrypted_api_key' ||
        k === 'authorization' ||
        k === 'api-key' ||
        k === 'token';
    }

    function maskSecret(value) {
      const text = String(value ?? '').trim();
      if (!text) return '***';
      if (text.length <= 8) return '***';
      return `${text.slice(0, 4)}...${text.slice(-2)}`;
    }

    function sanitizeForLog(value, keyHint = '') {
      if (value === null || value === undefined) return value;
      if (Array.isArray(value)) {
        return value.map((item) => sanitizeForLog(item, keyHint));
      }
      if (typeof value === 'object') {
        const out = {};
        for (const [key, nested] of Object.entries(value)) {
          out[key] = isSecretKeyName(key) ? maskSecret(nested) : sanitizeForLog(nested, key);
        }
        return out;
      }
      if (typeof value === 'string') {
        if (isSecretKeyName(keyHint)) return maskSecret(value);
        if (/^bearer\s+/i.test(value.trim())) return 'Bearer ***';
      }
      return value;
    }

    function setRunInProgress(isRunning, statusText) {
      runInProgress = !!isRunning;
      const runBtn = $('runBtn');
      runBtn.disabled = runInProgress;
      runBtn.textContent = runInProgress ? '执行中...' : '执行排班';
      if (runInProgress) {
        setStatus('warn', statusText || '正在执行排班，请稍候...');
      }
    }

    function scheduleLogRender() {
      if (logRenderScheduled) return;
      logRenderScheduled = true;
      const schedule = typeof window.requestAnimationFrame === 'function'
        ? window.requestAnimationFrame.bind(window)
        : (callback) => window.setTimeout(callback, 16);
      schedule(() => {
        logRenderScheduled = false;
        renderLogs();
      });
    }

    function flushPendingStreamChunks() {
      if (streamFlushTimer) {
        window.clearTimeout(streamFlushTimer);
        streamFlushTimer = 0;
      }
      if (pendingStreamStatus) {
        setRunInProgress(true, pendingStreamStatus || '正在接收模型流式输出...');
        pendingStreamStatus = '';
      }
      if (!pendingStreamText) {
        return;
      }
      $('runDetail').value += pendingStreamText;
      $('runDetail').scrollTop = $('runDetail').scrollHeight;
      pendingStreamText = '';
    }

    function queueStreamChunk(streamChunk, statusText) {
      if (typeof streamChunk === 'string' && streamChunk.length) {
        pendingStreamText += streamChunk;
      }
      if (typeof statusText === 'string' && statusText.trim()) {
        pendingStreamStatus = statusText.trim();
      }
      if (streamFlushTimer) {
        return;
      }
      streamFlushTimer = window.setTimeout(() => {
        streamFlushTimer = 0;
        flushPendingStreamChunks();
      }, STREAM_FLUSH_INTERVAL_MS);
    }

    function log(level, msg, payload) {
      const upper = String(level || 'INFO').toUpperCase();
      const entry = {
        time: now(),
        level: upper,
        text: String(msg || ''),
        payload: payload === undefined ? null : sanitizeForLog(payload)
      };
      logEntries.push(entry);
      if (logEntries.length > LOG_MAX) {
        logEntries.splice(0, logEntries.length - LOG_MAX);
      }
      scheduleLogRender();
    }

    function renderLogs() {
      const lines = [];
      for (const entry of logEntries) {
        if (logFilter === 'error' && entry.level !== 'ERROR') {
          continue;
        }
        const label = LOG_LEVEL_LABELS[entry.level] || entry.level;
        const extra = entry.payload == null ? '' : `\n${JSON.stringify(entry.payload, null, 2)}`;
        lines.push(`[${entry.time}] [${label}] ${entry.text}${extra}`);
      }
      $('log').textContent = lines.join('\n');
      $('log').scrollTop = $('log').scrollHeight;
    }

    function clearLogs() {
      logEntries.length = 0;
      renderLogs();
    }

    function toggleErrorLogFilter() {
      logFilter = logFilter === 'error' ? 'all' : 'error';
      $('errorLogBtn').textContent = logFilter === 'error' ? '仅错误日志：开启' : '仅错误日志：关闭';
      renderLogs();
      log('INFO', logFilter === 'error' ? '已切换为仅错误日志。' : '已切换为显示全部日志。');
    }

    function setStatus(type, text) {
      const tagMap = { ok: '成功', err: '错误', warn: '处理中' };
      $('runTag').textContent = tagMap[type] || '状态';
      const cls = type === 'ok' ? 'status ok' : type === 'err' ? 'status err' : 'status';
      const dot = type === 'ok' ? 'dot ok' : type === 'err' ? 'dot err' : 'dot warn';
      $('runStatus').className = cls;
      $('runStatus').innerHTML = `<span class="${dot}"></span><span>${html(text)}</span>`;
    }

    function setAutoApplyStatus(type, text) {
      const node = $('autoApplyStatus');
      if (!node) return;
      const cls = type === 'ok' ? 'status ok slim' : type === 'err' ? 'status err slim' : 'status slim';
      const dot = type === 'ok' ? 'dot ok' : type === 'err' ? 'dot err' : 'dot warn';
      node.className = cls;
      node.innerHTML = `<span class="${dot}"></span><span>${html(text)}</span>`;
    }

    function modeLabel(modeId) {
      return modeId === 'campus_6agent' ? '6Agent' : modeId === 'incremental_small' ? '增量小模型' : '标准';
    }

    function configBody(config) {
      const normalized = normalizeConfig(config);
      return {
        selected_plan_id: normalized.selected_plan_id,
        plan_presets: normalized.plan_presets.map((plan) => ({
          id: plan.id,
          name: plan.name,
          mode_id: plan.mode_id,
          api_key: plan.api_key,
          base_url: plan.base_url,
          model: plan.model,
          model_profile: plan.model_profile,
          provider_hint: plan.provider_hint,
          multi_agent_execution_mode: plan.multi_agent_execution_mode
        })),
        duty_rule: normalized.duty_rule
      };
    }

    function setBackendConfigDirtyState(isDirty, text) {
      backendConfigDirty = !!isDirty;
      if (typeof text === 'string' && text.trim()) {
        setAutoApplyStatus(isDirty ? 'warn' : 'ok', text);
        return;
      }
      setAutoApplyStatus(
        backendConfigDirty ? 'warn' : 'ok',
        backendConfigDirty ? '正式配置有未保存修改。' : '正式配置已与 FastAPI 同步。'
      );
    }

    function updateBackendConfigUiState() {
      const disableSave = runtimeMode !== 'backend' || !hasBackendTransport || !backendConfigDirty;
      const disableReload = runtimeMode !== 'backend' || !hasBackendTransport;
      $('saveConfigBtn').disabled = disableSave;
      $('reloadConfigBtn').disabled = disableReload;
      const selectedPlan = getSelectedPlan(store.config);
      const isMultiAgent = selectedPlan && selectedPlan.mode_id === 'campus_6agent';
      $('cfgMulti').disabled = !isMultiAgent;
    }

    function markBackendConfigDirty(reason) {
      const dirty = JSON.stringify(configBody(store.config)) !== JSON.stringify(configBody(lastLoadedBackendConfig));
      setBackendConfigDirtyState(dirty);
      if (reason) {
        log('INFO', dirty ? '正式配置已变更，等待保存。' : '正式配置已回到已保存状态。', { reason });
      }
      updateBackendConfigUiState();
    }

    function applyLocalConfigImmediate(reason, options) {
      const opts = options && typeof options === 'object' ? options : {};
      syncConfigFromForm();
      persistLocalDebugPrefs(reason, { quiet: true });
      if (opts.maskApiInput) {
        const selectedPlan = getSelectedPlan(store.config);
        $('cfgApi').value = String(selectedPlan?.api_key || '').trim() ? API_KEY_MASK : '';
      }
      if (!opts.quiet) {
        setAutoApplyStatus('ok', '本地调试项已更新并保存在当前设备。');
      }
      if (reason) {
        log('INFO', '本地调试配置已更新。', { reason });
      }
      updateBackendConfigUiState();
      return true;
    }

    function applyUnifiedSettingsDocument(document, reason) {
      const payload = document && typeof document === 'object' ? document : {};
      lastLoadedHostVersion = Math.max(1, int(payload.host_version, lastLoadedHostVersion || 1));
      store.config = normalizeConfig({
        ...store.config,
        ...(payload.backend && typeof payload.backend === 'object' ? payload.backend : {}),
        version: Math.max(1, int(payload.backend_version, store.config.version)),
        per_day: store.config.per_day,
        notification_templates: store.config.notification_templates
      });
      lastLoadedBackendConfig = clone(store.config);
      persistLocalDebugPrefs(`settings_sync:${reason || 'manual'}`, { quiet: true });
      renderConfig();
      setBackendConfigDirtyState(false, '正式配置已与 FastAPI 同步。');
    }

    async function loadSettingsFromBackend(reason) {
      if (!hasBackendTransport) {
        setAutoApplyStatus('err', '当前页面未连接到 FastAPI。');
        return false;
      }
      const endpoint = `${window.location.origin}/api/v1/settings`;
      try {
        log('INFO', '开始从 FastAPI 加载统一设置。', { reason: reason || 'manual', endpoint });
        const response = await fetch(endpoint, {
          headers: {
            'Accept': 'application/json',
            'X-Duty-Request-Source': 'web_app'
          }
        });
        const data = await readResponsePayload(response);
        if (!response.ok) {
          throw new Error(responseErrorMessage(response, data));
        }
        applyUnifiedSettingsDocument(data, reason || 'manual');
        log('OK', 'FastAPI 统一设置加载成功。', {
          reason: reason || 'manual',
          host_version: lastLoadedHostVersion,
          version: store.config.version,
          selected_plan_id: store.config.selected_plan_id
        });
        return true;
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        setAutoApplyStatus('err', `统一设置加载失败：${message}`);
        log('ERROR', 'FastAPI 统一设置加载失败。', { reason: reason || 'manual', message, endpoint });
        return false;
      }
    }

    function buildBackendConfigPatch() {
      syncConfigFromForm();
      const currentBody = configBody(store.config);
      const loadedBody = configBody(lastLoadedBackendConfig);
      const backendPatch = {};
      let hasChanges = false;
      for (const key of ['selected_plan_id', 'plan_presets', 'duty_rule']) {
        if (JSON.stringify(currentBody[key]) !== JSON.stringify(loadedBody[key])) {
          backendPatch[key] = currentBody[key];
          hasChanges = true;
        }
      }
      if (!hasChanges) return null;
      return {
        expected: {
          host_version: lastLoadedHostVersion,
          backend_version: lastLoadedBackendConfig.version
        },
        changes: {
          backend: backendPatch
        }
      };
    }

    function queueBackendSettingsAutoSave(reason, options) {
      const opts = options && typeof options === 'object' ? options : {};
      if (runtimeMode !== 'backend' || !hasBackendTransport) return;
      syncConfigFromForm();
      markBackendConfigDirty(reason);
      if (!backendConfigDirty) return;
      settingsSaveQueued = true;
      if (settingsAutoSaveTimer) {
        clearTimeout(settingsAutoSaveTimer);
      }
      const delay = opts.immediate ? 0 : 500;
      settingsAutoSaveTimer = window.setTimeout(() => {
        settingsAutoSaveTimer = 0;
        void flushBackendSettingsAutoSave(reason || 'auto');
      }, delay);
      setAutoApplyStatus('warn', '正式配置已变更，正在自动同步到 FastAPI...');
    }

    async function flushBackendSettingsAutoSave(reason) {
      if (settingsSaveInFlight) {
        settingsSaveQueued = true;
        return false;
      }

      settingsSaveInFlight = true;
      settingsSaveQueued = false;
      try {
        return await saveConfigToBackend(reason || 'auto');
      } finally {
        settingsSaveInFlight = false;
        if (settingsSaveQueued || backendConfigDirty) {
          settingsSaveQueued = false;
          queueBackendSettingsAutoSave('requeue');
        }
      }
    }

    async function saveConfigToBackend(reason) {
      if (!hasBackendTransport) {
        setAutoApplyStatus('err', '当前页面未连接到 FastAPI。');
        return false;
      }
      const patch = buildBackendConfigPatch();
      if (!patch) {
        setBackendConfigDirtyState(false, '正式配置没有变化。');
        return true;
      }
      const endpoint = `${window.location.origin}/api/v1/settings`;
      $('saveConfigBtn').disabled = true;
      try {
        log('INFO', '开始保存统一设置到 FastAPI。', {
          reason: reason || 'manual',
          endpoint,
          patch_keys: Object.keys((patch.changes && patch.changes.backend) || {})
        });
        const response = await fetch(endpoint, {
          method: 'PATCH',
          headers: {
            'Accept': 'application/json',
            'Content-Type': 'application/json',
            'X-Duty-Request-Source': 'web_app'
          },
          body: JSON.stringify(patch)
        });
        const data = await readResponsePayload(response);
        if (data && data.document) {
          applyUnifiedSettingsDocument(data.document, reason || 'save');
        }
        if (!response.ok || !data.success) {
          const detail = responseErrorMessage(response, data);
          setAutoApplyStatus('err', `统一设置保存失败：${detail}`);
          log('ERROR', 'FastAPI 统一设置保存失败。', {
            reason: reason || 'manual',
            message: detail,
            endpoint,
            expected_host_version: patch.expected.host_version,
            expected_backend_version: patch.expected.backend_version
          });
          return false;
        }
        persistLocalDebugPrefs(`save_config:${reason || 'manual'}`, { quiet: true });
        setBackendConfigDirtyState(false, `正式配置已自动保存（版本 ${store.config.version}）。`);
        log('OK', 'FastAPI 统一设置保存成功。', {
          reason: reason || 'manual',
          host_version: lastLoadedHostVersion,
          version: store.config.version,
          selected_plan_id: store.config.selected_plan_id
        });
        return true;
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        setAutoApplyStatus('err', `统一设置保存失败：${message}`);
        log('ERROR', 'FastAPI 统一设置保存失败。', {
          reason: reason || 'manual',
          message,
          endpoint,
          expected_host_version: patch.expected.host_version,
          expected_backend_version: patch.expected.backend_version
        });
        return false;
      } finally {
        updateBackendConfigUiState();
      }
    }

    async function saveRosterToBackend(rosterSnapshot, reason, options) {
      const opts = options && typeof options === 'object' ? options : {};
      if (!hasBackendTransport) {
        setAutoApplyStatus('err', '名单同步失败：当前页面未连接到 FastAPI。');
        log('ERROR', 'FastAPI 名单保存失败：页面不在 HTTP 环境中。', { reason: reason || 'unknown' });
        return false;
      }

      const endpoint = `${window.location.origin}/api/v1/roster`;
      const traceId = createClientTraceId('roster');
      try {
        log('INFO', '开始保存名单到 FastAPI。', {
          reason: reason || 'unknown',
          endpoint,
          trace_id: traceId,
          roster_count: rosterSnapshot.length
        });
        const response = await fetch(endpoint, {
          method: 'PUT',
          headers: {
            'Accept': 'application/json',
            'Content-Type': 'application/json',
            'X-Duty-Trace-Id': traceId,
            'X-Duty-Request-Source': 'web_app'
          },
          body: JSON.stringify({ roster: rosterSnapshot })
        });
        const data = await readResponsePayload(response);
        if (!response.ok) {
          throw new Error(responseErrorMessage(response, data));
        }

        const savedRoster = normalizeRoster(data.roster);
        store.roster = savedRoster;
        if (selectedRosterId != null && !savedRoster.some((entry) => entry.id === selectedRosterId)) {
          selectedRosterId = null;
        }
        renderRoster();
        setAutoApplyStatus('ok', '名单已同步到 FastAPI。');
        if (!opts.quiet) {
          setStatus('ok', '名单已保存到 FastAPI。');
        }
        log('OK', 'FastAPI 名单保存成功。', {
          reason: reason || 'unknown',
          trace_id: traceId,
          roster_count: savedRoster.length,
          active_count: savedRoster.filter((entry) => entry.active).length
        });
        return true;
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        setAutoApplyStatus('err', `名单同步失败：${message}`);
        log('ERROR', 'FastAPI 名单保存失败。', {
          reason: reason || 'unknown',
          trace_id: traceId,
          message,
          roster_count: rosterSnapshot.length
        });
        return false;
      }
    }

    function applyRosterImmediate(reason, options) {
      const opts = options && typeof options === 'object' ? options : {};
      if (runtimeMode === 'backend') {
        const rosterSnapshot = normalizeRoster(store.roster);
        if (!opts.quiet) {
          setAutoApplyStatus('warn', '名单修改已记录，正在同步到 FastAPI。');
        }
        pendingRosterSavePromise = pendingRosterSavePromise
          .catch(() => false)
          .then(() => saveRosterToBackend(rosterSnapshot, reason, opts));
        return true;
      }
      if (!opts.quiet) {
        setAutoApplyStatus('ok', '名单已自动应用（本地模式）。');
      }
      return true;
    }

    function bridgePost(action, payload) {
      const msg = { source: 'duty-agent-test-page', action, payload: payload || {} };
      if (!hasBridge) { log('WARN', `宿主不可用：${action}`, msg); return false; }
      window.chrome.webview.postMessage(msg);
      log('SEND', `发送到宿主：${action}`, msg.payload);
      return true;
    }

    function setMode(mode) {
      runtimeMode = mode === 'backend' ? 'backend' : 'mock';
      $('mode').value = runtimeMode;
      $('runtimeTag').textContent = runtimeMode === 'backend'
        ? '运行模式：FastAPI 模式'
        : '运行模式：本地模拟';
      log('INFO', `切换运行模式：${runtimeMode === 'backend' ? 'FastAPI 模式' : '本地模拟'}`);
      $('bridgeDot').className = hasBridge ? 'dot ok' : 'dot';
      $('bridgeText').textContent = hasBridge ? '宿主能力可用' : '宿主能力未启用';
      if (runtimeMode === 'backend') {
        setBackendConfigDirtyState(backendConfigDirty);
      } else {
        setAutoApplyStatus('ok', '当前为本地模拟模式；正式配置不会自动写回后端。');
      }
      updateBackendConfigUiState();
    }

    function renderOptions(selectId, values) {
      const el = $(selectId);
      el.innerHTML = '';
      for (const v of values) {
        const op = document.createElement('option');
        op.value = v;
        op.textContent = v;
        el.appendChild(op);
      }
      if (el.options.length) el.selectedIndex = 0;
    }

    function renderConfig() {
      const c = store.config;
      const selectedPlan = getSelectedPlan(c);
      const planSelect = $('cfgPlan');
      planSelect.innerHTML = '';
      for (const plan of c.plan_presets) {
        const option = document.createElement('option');
        option.value = plan.id;
        option.textContent = `${plan.name} (${modeLabel(plan.mode_id)})`;
        planSelect.appendChild(option);
      }
      planSelect.value = c.selected_plan_id;
      planSelect.dataset.currentId = c.selected_plan_id;
      $('cfgPlanName').value = selectedPlan.name;
      $('cfgPlanMode').value = selectedPlan.mode_id;
      $('cfgApi').value = String(selectedPlan.api_key || '').trim() ? API_KEY_MASK : '';
      $('cfgBase').value = selectedPlan.base_url;
      $('cfgModel').value = selectedPlan.model;
      $('cfgProfile').value = selectedPlan.model_profile;
      $('cfgProvider').value = selectedPlan.provider_hint;
      $('cfgMulti').value = selectedPlan.multi_agent_execution_mode;
      $('cfgPer').value = c.per_day;
      $('cfgRule').value = c.duty_rule;
      renderOptions('tplList', c.notification_templates);
      updateBackendConfigUiState();
    }

    function syncConfigFromForm() {
      const inputApiKey = $('cfgApi').value;
      const keepCurrentMasks = new Set([API_KEY_MASK, LEGACY_API_KEY_MASK, HOST_API_KEY_MASK]);
      const currentConfig = normalizeConfig(store.config);
      const selectedPlanId = String($('cfgPlan').dataset.currentId || currentConfig.selected_plan_id || '').trim() || currentConfig.selected_plan_id;
      const nextSelectedPlanId = String($('cfgPlan').value || selectedPlanId).trim() || selectedPlanId;
      const plans = clone(currentConfig.plan_presets);
      const selectedIndex = Math.max(0, plans.findIndex((plan) => plan.id === selectedPlanId));
      const existingPlan = selectedIndex >= 0 ? plans[selectedIndex] : plans[0];
      const apiKey = keepCurrentMasks.has(inputApiKey)
        ? (String(existingPlan?.api_key || '').trim() || HOST_API_KEY_MASK)
        : inputApiKey;
      const nextPlan = {
        ...existingPlan,
        name: $('cfgPlanName').value,
        mode_id: $('cfgPlanMode').value,
        api_key: apiKey,
        base_url: $('cfgBase').value,
        model: $('cfgModel').value,
        model_profile: $('cfgProfile').value,
        provider_hint: $('cfgProvider').value,
        multi_agent_execution_mode: $('cfgMulti').value
      };
      plans[selectedIndex >= 0 ? selectedIndex : 0] = nextPlan;
      store.config = normalizeConfig({
        ...currentConfig,
        selected_plan_id: nextSelectedPlanId,
        plan_presets: plans,
        per_day: $('cfgPer').value,
        duty_rule: $('cfgRule').value,
        notification_templates: Array.from($('tplList').options).map((x) => x.value)
      });
    }

    function rosterStats() {
      const cnt = new Map();
      const nxt = new Map();
      const td = today();
      const pool = [...store.state.schedule_pool].sort((a, b) => a.date.localeCompare(b.date));

      for (const item of pool) {
        const d = parseIso(item.date);
        if (!d) continue;
        const set = new Set();
        const map = assignment(item);
        for (const arr of Object.values(map)) for (const n of arr) if (n) set.add(n);
        for (const n of set) {
          cnt.set(n, (cnt.get(n) || 0) + 1);
          if (d >= td) {
            const old = nxt.get(n);
            if (!old || d < old) nxt.set(n, d);
          }
        }
      }

      const rows = store.roster.map((r) => ({
        id: r.id,
        name: r.name,
        active: r.active,
        next: nxt.get(r.name) ? dateIso(nxt.get(r.name)) : '未安排',
        count: cnt.get(r.name) || 0
      }));

      rows.sort((a, b) => {
        const au = a.next === '未安排';
        const bu = b.next === '未安排';
        if (au !== bu) return au ? 1 : -1;
        if (!au && a.next !== b.next) return a.next.localeCompare(b.next);
        return a.id - b.id;
      });
      return rows;
    }

    function renderRoster() {
      const rows = rosterStats();
      $('rosterBody').innerHTML = '';
      for (const r of rows) {
        const tr = document.createElement('tr');
        tr.dataset.id = String(r.id);
        if (r.id === selectedRosterId) tr.className = 'sel';
        tr.innerHTML = `<td class="mono">${r.id}</td><td>${html(r.name)}</td><td class="mono">${html(r.next)}</td><td class="mono">${r.count}</td><td><label class="active-cell"><input class="roster-active" type="checkbox" ${r.active ? 'checked' : ''}><span>${r.active ? '启用' : '停用'}</span></label></td>`;
        const activeInput = tr.querySelector('.roster-active');
        if (activeInput) {
          activeInput.addEventListener('click', (event) => event.stopPropagation());
          activeInput.addEventListener('change', () => {
            const target = store.roster.find((x) => x.id === r.id);
            if (!target) return;
            target.active = !!activeInput.checked;
            renderRoster();
            applyRosterImmediate('toggle_active', { quiet: true });
            setStatus('ok', `已${target.active ? '启用' : '停用'}：${target.name}`);
          });
        }
        tr.addEventListener('click', () => { selectedRosterId = r.id; renderRoster(); });
        $('rosterBody').appendChild(tr);
      }
    }

    function allAreaNames() {
      const out = [];
      const set = new Set(out);
      for (const item of store.state.schedule_pool) {
        for (const k of Object.keys(assignment(item))) {
          if (!set.has(k)) { set.add(k); out.push(k); }
        }
      }
      return out;
    }
    function renderSchedule() {
      const areas = allAreaNames();
      $('scheduleHead').innerHTML = `<tr><th>日期</th><th>星期</th>${areas.map((a) => `<th>${html(a)}</th>`).join('')}</tr>`;
      const pool = [...store.state.schedule_pool].sort((a, b) => a.date.localeCompare(b.date));
      if (!pool.length) {
        $('scheduleBody').innerHTML = `<tr><td colspan="${2 + areas.length}">暂无排班数据</td></tr>`;
        return;
      }
      $('scheduleBody').innerHTML = '';
      for (const it of pool) {
        const map = assignment(it, areas);
        const tr = document.createElement('tr');
        const cells = [`<td class="mono">${html(it.date)}</td>`, `<td class="mono">${html(it.day)}</td>`];
        for (const a of areas) cells.push(`<td>${html((map[a] || []).join(', ') || '-')}</td>`);
        tr.innerHTML = cells.join('');
        $('scheduleBody').appendChild(tr);
      }
    }

    function renderLegacy() {
      const pool = [...store.state.schedule_pool].sort((a, b) => a.date.localeCompare(b.date));
      $('legacyBody').innerHTML = '';
      if (!pool.length) {
        $('legacyBody').innerHTML = '<tr><td colspan="3">暂无排班数据</td></tr>';
        return;
      }
      for (const it of pool) {
        const map = assignment(it);
        const c1 = map['教室'] || names(it.classroom_students);
        const c2 = map['清洁区'] || names(it.cleaning_area_students);
        const tr = document.createElement('tr');
        tr.innerHTML = `<td class="mono">${html(it.date)}</td><td>${html(c1.join(', ') || '-')}</td><td>${html(c2.join(', ') || '-')}</td>`;
        $('legacyBody').appendChild(tr);
      }
    }

    function renderAll() {
      renderConfig();
      renderRoster();
      renderSchedule();
      renderLegacy();
      $('runtimeTag').textContent = runtimeMode === 'backend'
        ? '运行模式：FastAPI 模式'
        : '运行模式：本地模拟';
    }

    async function loadSnapshotFromBackend(reason) {
      if (!hasBackendTransport) {
        setStatus('err', '当前页面未连接到 FastAPI。');
        log('ERROR', 'FastAPI 快照加载失败：页面不在 HTTP 环境中。', { reason: reason || 'unknown' });
        return false;
      }

      const endpoint = `${window.location.origin}/api/v1/snapshot`;
      try {
        log('INFO', '开始从 FastAPI 加载快照。', { reason: reason || 'manual', endpoint });
        const response = await fetch(endpoint, {
          headers: {
            'Accept': 'application/json',
            'X-Duty-Request-Source': 'web_app'
          }
        });
        const data = await readResponsePayload(response);
        if (!response.ok) {
          throw new Error(responseErrorMessage(response, data));
        }

        store = {
          config: normalizeConfig(store.config),
          roster: normalizeRoster(data.roster),
          state: normalizeState(data.state)
        };
        pendingRosterSavePromise = Promise.resolve(true);
        selectedRosterId = null;
        renderAll();
        setStatus('ok', '已从 FastAPI 加载最新数据。');
        log('OK', 'FastAPI 快照加载成功。', {
          reason: reason || 'manual',
          version: store.config.version,
          roster_count: store.roster.length,
          schedule_count: store.state.schedule_pool.length
        });
        return true;
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        setStatus('err', `FastAPI 快照加载失败：${message}`);
        log('ERROR', 'FastAPI 快照加载失败。', { reason: reason || 'manual', message, endpoint });
        return false;
      }
    }

    function startDateFromConfig(applyMode) {
      const pool = [...store.state.schedule_pool].sort((a, b) => a.date.localeCompare(b.date));
      if (applyMode === 'append') {
        if (pool.length) {
          const last = parseIso(pool[pool.length - 1].date);
          if (last) return addDays(last, 1);
        }
        return today();
      }
      return today();
    }

    function targetDates(start, days) {
      const out = [];
      let d = new Date(start);
      while (out.length < days) {
        out.push(new Date(d));
        d = addDays(d, 1);
      }
      return out;
    }

    function lastAssignedName(pool, areas, applyMode, startDate) {
      const start = dateIso(startDate);
      const src = applyMode === 'append' ? pool : pool.filter((x) => x.date < start);
      for (let i = src.length - 1; i >= 0; i--) {
        const map = assignment(src[i], areas);
        const keys = [...areas, ...Object.keys(map).filter((k) => !areas.includes(k))];
        for (const k of keys) {
          const arr = map[k] || [];
          if (arr.length) return arr[arr.length - 1];
        }
      }
      return null;
    }

    function dedupeByDate(items) {
      const map = new Map();
      for (const item of items) {
        const key = String(item?.date ?? '').trim();
        if (!key) continue;
        map.set(key, item);
      }
      return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([, v]) => v);
    }

    function mergeSchedule(existing, generated, applyMode, startDate) {
      const cur = [...existing].sort((a, b) => a.date.localeCompare(b.date));
      const start = dateIso(startDate);
      const td = dateIso(today());
      if (applyMode === 'replace_all') return dedupeByDate([...generated]);
      if (applyMode === 'replace_future') return dedupeByDate([...cur.filter((x) => x.date < start), ...generated]);
      if (applyMode === 'replace_overlap') {
        const end = generated.length ? generated[generated.length - 1].date : start;
        return dedupeByDate([...cur.filter((x) => x.date < start || x.date > end), ...generated]);
      }
      return dedupeByDate([...cur, ...generated]);
    }

    function runMockInternal(daysToGenerate, applyMode, instruction, detailText) {
      syncConfigFromForm();
      const areas = allAreaNames();
      if (!areas.length) areas.push('default_area');
      const per = clamp(int(store.config.per_day, 2), 1, 30);
      const areaCounts = Object.fromEntries(areas.map((area) => [area, per]));
      const totalPerDay = Object.values(areaCounts).reduce((sum, value) => sum + value, 0);
      const days = clamp(int(daysToGenerate, 1), 1, 30);
      const active = store.roster.filter((r) => r.active && r.name).sort((a, b) => a.id - b.id);
      if (!active.length) {
        setStatus('err', '没有可排班的启用学生。');
        log('ERROR', '模拟排班失败：没有可排班的启用学生。');
        return;
      }

      const start = startDateFromConfig(applyMode);
      const dates = targetDates(start, days);
      const anchor = lastAssignedName(store.state.schedule_pool, areas, applyMode, start);
      let idx = active.length - 1;
      if (anchor) {
        const p = active.findIndex((x) => x.name === anchor);
        if (p >= 0) idx = p;
      }
      let cursor = (idx + 1) % active.length;

      const generated = [];
      for (const d of dates) {
        const used = new Set();
        const map = {};
        for (const area of areas) {
          const arr = [];
          const requiredCount = areaCounts[area];
          const noDupAcrossArea = totalPerDay <= active.length;
          let guard = 0;
          while (arr.length < requiredCount && guard < active.length * 4) {
            const stu = active[cursor];
            cursor = (cursor + 1) % active.length;
            guard++;
            if (!stu || arr.includes(stu.name)) continue;
            if (noDupAcrossArea && used.has(stu.name)) continue;
            arr.push(stu.name);
            used.add(stu.name);
          }
          while (arr.length < requiredCount) {
            const stu = active[cursor];
            cursor = (cursor + 1) % active.length;
            if (!stu || arr.includes(stu.name)) break;
            arr.push(stu.name);
          }
          map[area] = arr;
        }

        const a1 = areas[0];
        const a2 = areas.length > 1 ? areas[1] : a1;
        generated.push({
          date: dateIso(d),
          day: DAY[d.getDay()],
          students: map[a1] ? [...map[a1]] : [],
          classroom_students: map[a1] ? [...map[a1]] : [],
          cleaning_area_students: map[a2] ? [...map[a2]] : [],
          area_assignments: map
        });
      }

      store.state.schedule_pool = mergeSchedule(store.state.schedule_pool, generated, applyMode, start);
      store.state = normalizeState(store.state);
      renderAll();
      setStatus('ok', `模拟排班成功，生成 ${generated.length} 天。`);
      log('OK', '模拟排班成功。', {
        generated_days: generated.length,
        apply_mode: applyMode,
        areas,
        per_day: per
      });

      notifyByTemplates(
        'manual',
        true,
        detailText || '模拟执行成功',
        instruction,
        applyMode,
        clamp(int($('nDuration').value, 7), 1, 30),
        false
      );
    }

    function runMock() {
      const instruction = $('instruction').value.trim();
      const applyMode = $('applyMode').value;
      if (!instruction) {
        setStatus('err', '请输入排班指令。');
        log('ERROR', '模拟排班失败：排班指令为空。');
        return;
      }

      const days = MOCK_GENERATION_DAYS;
      log('INFO', '开始执行模拟排班。', { mode: applyMode, days });
      runMockInternal(days, applyMode, instruction, $('runDetail').value.trim() || '模拟执行成功');
    }

    function renderTpl(tpl, tokens) {
      return String(tpl || '').replace(/\{([a-zA-Z0-9_]+)\}/g, (_, k) => (k in tokens ? tokens[k] : `{${k}}`)).trim();
    }

    function notifyByTemplates(scene, success, detail, instruction, applyMode, duration, sendHost) {
      syncConfigFromForm();
      const sceneText = scene === 'auto' ? '自动排班' : '手动排班';
      const statusText = success ? '成功' : '失败';
      const tok = {
        scene: sceneText,
        status: statusText,
        date: `${dateIso(today())} ${new Date().toTimeString().slice(0, 5)}`,
        areas: allAreaNames().join('、'),
        per_day: String(store.config.per_day),
        mode: applyMode || $('applyMode').value,
        instruction: String(instruction || '').trim().slice(0, 80),
        message: String(detail || '').trim().slice(0, 100)
      };

      const seen = new Set();
      const msgs = [];
      for (const tpl of list(store.config.notification_templates, DEFAULT_CONFIG.notification_templates)) {
        const txt = renderTpl(tpl, tok);
        if (!txt || seen.has(txt)) continue;
        seen.add(txt);
        msgs.push(txt);
      }
      if (!msgs.length) msgs.push(`${sceneText}${statusText}，日期：${tok.date}`);

      $('noticeList').innerHTML = '';
      for (const m of msgs) {
        const div = document.createElement('div');
        div.className = `notice ${success ? '' : 'fail'}`.trim();
        div.textContent = m;
        $('noticeList').appendChild(div);
        if (sendHost && hasBridge) {
          bridgePost('publish_notification', { text: m, duration_seconds: duration });
        }
      }

      log('INFO', `通知模板渲染完成 (${msgs.length} 条)。`, { scene, success, tokens: tok });
    }

    function triggerRunCompletionNotification() {
      const payload = {
        instruction: $('instruction').value.trim(),
        apply_mode: $('applyMode').value,
        message: $('runDetail').value.trim() || $('nMsg').value.trim()
      };
      const ok = bridgePost('trigger_run_completion_notification', payload);
      if (ok) {
        setStatus('warn', '已触发完成排班通知。');
        log('OK', '已请求宿主触发完成排班通知。', payload);
      } else {
        setStatus('err', '宿主不可用，无法触发完成排班通知。');
        log('ERROR', '触发完成排班通知失败：宿主不可用。');
      }
    }

    function triggerDutyReminderNotification() {
      const nowDate = dateIso(today());
      const nowTime = new Date().toTimeString().slice(0, 5);
      const payload = { date: nowDate, time: nowTime };
      const ok = bridgePost('trigger_duty_reminder_notification', payload);
      if (ok) {
        setStatus('warn', '已触发值日提醒通知。');
        log('OK', '已请求宿主触发值日提醒通知。', payload);
      } else {
        setStatus('err', '宿主不可用，无法触发值日提醒通知。');
        log('ERROR', '触发值日提醒通知失败：宿主不可用。');
      }
    }

    function openTestInBrowser() {
      if (hasBridge) {
        const ok = bridgePost('open_test_in_browser', {});
        if (ok) {
          setStatus('warn', '已请求宿主在本地浏览器打开当前页面。');
          log('OK', '已请求宿主在本地浏览器打开当前页面。');
        } else {
          setStatus('err', '宿主不可用，无法请求打开。');
          log('ERROR', '请求宿主打开浏览器失败：宿主不可用。');
        }
        return;
      }

      const target = window.location.href;
      window.open(target, '_blank');
      setStatus('ok', '已在浏览器打开当前页面。');
      log('OK', '已在浏览器打开当前页面。', { target });
    }

    function handleBackendRunProgress(progress) {
      const phase = String(progress?.phase || '').trim().toLowerCase();
      const message = String(progress?.message || '').trim();
      const streamChunk = String(progress?.stream_chunk || '');
      if (phase === 'stream_chunk') {
        queueStreamChunk(streamChunk, message || '正在接收模型流式输出...');
        return;
      }
      setRunInProgress(true, message || '正在执行排班，请稍候...');
      log('INFO', 'FastAPI 执行状态更新。', { phase, message, stream_chunk: streamChunk });
    }

    function parseSseMessage(block) {
      const text = String(block || '').replace(/\r/g, '');
      if (!text.trim()) return null;
      const lines = text.split('\n');
      let eventName = '';
      const dataLines = [];
      for (const rawLine of lines) {
        if (rawLine.startsWith('event:')) {
          eventName = rawLine.slice(6).trim();
        } else if (rawLine.startsWith('data:')) {
          dataLines.push(rawLine.slice(5).trimStart());
        }
      }
      if (!dataLines.length) return null;
      const rawData = dataLines.join('\n');
      let payload = {};
      try {
        payload = rawData ? JSON.parse(rawData) : {};
      } catch {
        payload = { message: rawData };
      }
      return { event: eventName || 'message', data: payload };
    }

    async function readScheduleEventStream(response) {
      const reader = response.body && typeof response.body.getReader === 'function'
        ? response.body.getReader()
        : null;
      if (!reader) {
        throw new Error('当前浏览器不支持流式读取排班结果。');
      }

      const decoder = new TextDecoder();
      let buffer = '';
      let finalPayload = null;

      while (true) {
        const { value, done } = await reader.read();
        buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
        buffer = buffer.replace(/\r\n/g, '\n');

        let boundary = buffer.indexOf('\n\n');
        while (boundary >= 0) {
          const block = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const message = parseSseMessage(block);
          if (message) {
            if (message.event === 'complete') {
              finalPayload = message.data;
            } else {
              handleBackendRunProgress(message.data);
            }
          }
          boundary = buffer.indexOf('\n\n');
        }

        if (done) {
          break;
        }
      }

      if (buffer.trim()) {
        const trailing = parseSseMessage(buffer);
        if (trailing) {
          if (trailing.event === 'complete') {
            finalPayload = trailing.data;
          } else {
            handleBackendRunProgress(trailing.data);
          }
        }
      }

      return finalPayload;
    }

    async function runBackend() {
      if (runInProgress) {
        setStatus('warn', '已有排班任务在执行中，请稍候。');
        log('WARN', '忽略 FastAPI 排班请求：已有任务进行中。');
        return;
      }
      syncConfigFromForm();
      const instruction = $('instruction').value.trim();
      const applyMode = $('applyMode').value;
      if (!instruction) {
        setStatus('err', '请输入排班指令。');
        log('ERROR', 'FastAPI 排班失败：排班指令为空。');
        return;
      }
      if (backendConfigDirty) {
        setStatus('warn', '正式配置有未保存修改，请先保存配置后再执行排班。');
        log('WARN', '拒绝 FastAPI 排班请求：正式配置尚未保存。');
        return;
      }
      const rosterSaved = await pendingRosterSavePromise.catch(() => false);
      if (!rosterSaved) {
        setStatus('err', '名单尚未成功同步到 FastAPI，请先处理名单保存错误。');
        log('ERROR', '拒绝 FastAPI 排班请求：名单保存失败。');
        return;
      }
      if (!hasBackendTransport) {
        setStatus('err', '当前页面未连接到 FastAPI。');
        log('ERROR', 'FastAPI 排班失败：页面不在 HTTP 环境中。');
        return;
      }

      const endpoint = `${window.location.origin}/api/v1/duty/schedule`;
      const traceId = createClientTraceId('run');
      const payload = {
        instruction,
        apply_mode: applyMode,
        trace_id: traceId,
        request_source: 'web_app'
      };

      try {
        log('INFO', '开始请求 FastAPI 执行排班。', {
          endpoint,
          trace_id: traceId,
          apply_mode: applyMode
        });
        setRunInProgress(true, '正在执行排班，请稍候...');
        const response = await fetch(endpoint, {
          method: 'POST',
          headers: {
            'Accept': 'text/event-stream',
            'Content-Type': 'application/json',
            'X-Duty-Trace-Id': traceId,
            'X-Duty-Request-Source': 'web_app'
          },
          body: JSON.stringify(payload)
        });

        if (!response.ok) {
          const errorPayload = await readResponsePayload(response);
          throw new Error(responseErrorMessage(response, errorPayload));
        }

        const finalPayload = await readScheduleEventStream(response);
        if (!finalPayload || typeof finalPayload !== 'object') {
          throw new Error('排班事件流提前结束，未收到最终结果。');
        }

        const ok = String(finalPayload.status || '').trim().toLowerCase() === 'success';
        const message = String(finalPayload.message || '').trim();
        const aiResponse = String(finalPayload.ai_response || '').trim();
        if (message) {
          $('nMsg').value = message;
        }
        if (aiResponse) {
          log('INFO', 'FastAPI AI 返回内容。', { ai_response: aiResponse });
        }

        if (!ok) {
          throw new Error(message || '未知错误');
        }

        flushPendingStreamChunks();
        setRunInProgress(false);
        log('OK', 'FastAPI 排班成功。', {
          trace_id: traceId,
          selected_executor: finalPayload.selected_executor || '',
          status: finalPayload.status || 'success'
        });
        const refreshed = await loadSnapshotFromBackend('run_complete');
        if (refreshed) {
          setStatus('ok', 'FastAPI 排班成功。');
        } else {
          setStatus('warn', 'FastAPI 排班成功，但刷新最新快照失败，请手动加载数据。');
          log('WARN', 'FastAPI 排班成功后刷新快照失败。', { trace_id: traceId });
        }
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        flushPendingStreamChunks();
        setRunInProgress(false);
        setStatus('err', `FastAPI 排班失败：${message}`);
        log('ERROR', 'FastAPI 排班失败。', { trace_id: traceId, message });
      }
    }

    function addTpl() {
      const text = $('newTpl').value.trim();
      if (!text) {
        setStatus('err', '模板不能为空。');
        log('ERROR', '新增通知模板失败：模板为空。');
        return;
      }
      syncConfigFromForm();
      if (store.config.notification_templates.includes(text)) {
        setStatus('err', '模板已存在。');
        log('ERROR', '新增通知模板失败：模板已存在。');
        return;
      }
      store.config.notification_templates.push(text);
      $('newTpl').value = '';
      renderConfig();
      applyLocalConfigImmediate('add_template', { quiet: true });
      setStatus('ok', '通知模板已新增。');
      log('OK', '已新增通知模板。', { template: text });
    }

    function delTpl() {
      syncConfigFromForm();
      const v = $('tplList').value;
      if (!v) {
        setStatus('err', '请先选择模板。');
        log('ERROR', '删除通知模板失败：未选择模板。');
        return;
      }
      if (store.config.notification_templates.length <= 1) {
        setStatus('err', '至少保留一个模板。');
        log('ERROR', '删除通知模板失败：模板数量不能少于 1。');
        return;
      }
      store.config.notification_templates = store.config.notification_templates.filter((x) => x !== v);
      renderConfig();
      applyLocalConfigImmediate('del_template', { quiet: true });
      setStatus('ok', '通知模板已删除。');
      log('OK', '已删除通知模板。', { template: v });
    }

    function addStudent() {
      const baseName = $('newStudent').value.trim();
      if (!baseName) {
        setStatus('err', '学生姓名不能为空。');
        log('ERROR', '新增学生失败：姓名为空。');
        return;
      }

      const existing = store.roster.some((x) => String(x?.name ?? '').trim() === baseName);
      if (existing) {
        setStatus('err', `添加失败：检测到同名学生 "${baseName}"`);
        log('ERROR', '添加学生失败：同名冲突。');
        return;
      }

      const nextId = store.roster.length ? Math.max(...store.roster.map((x) => x.id)) + 1 : 1;
      store.roster.push({ id: nextId, name: baseName, active: true });
      $('newStudent').value = '';
      selectedRosterId = nextId;
      renderRoster();
      applyRosterImmediate('add_student', { quiet: true });
      setStatus('ok', `已添加学生：${baseName} (ID ${nextId})`);
      log('OK', '已添加学生。', { id: nextId, requested: baseName });
    }

    function delStudent() {
      if (selectedRosterId == null) {
        setStatus('err', '请先在表格中选择学生。');
        log('ERROR', '删除学生失败：未选择学生。');
        return;
      }
      const before = store.roster.length;
      store.roster = store.roster.filter((x) => x.id !== selectedRosterId);
      if (store.roster.length === before) {
        setStatus('err', '未找到选中学生。');
        log('ERROR', '删除学生失败：未找到对应 ID。', { id: selectedRosterId });
        return;
      }
      selectedRosterId = null;
      renderRoster();
      applyRosterImmediate('del_student', { quiet: true });
      setStatus('ok', '已删除选中学生。');
      log('OK', '已删除学生。');
    }

    function exportSnapshot() {
      syncConfigFromForm();
      const out = clone(store);
      if (Array.isArray(out.config.plan_presets)) {
        out.config.plan_presets = out.config.plan_presets.map((plan) => ({
          ...plan,
          api_key: String(plan.api_key || '').trim() ? API_KEY_MASK : ''
        }));
      }
      $('snapshot').value = JSON.stringify(out, null, 2);
      setStatus('ok', '快照导出成功。');
      log('INFO', '快照已导出（API Key 已掩码）。');
    }

    function importSnapshot() {
      const txt = $('snapshot').value.trim();
      if (!txt) {
        setStatus('err', '请先粘贴快照 JSON。');
        log('ERROR', '导入快照失败：输入为空。');
        return;
      }
      try {
        const parsed = JSON.parse(txt);
        const cfg = normalizeConfig(parsed.config);
        const previousPlans = normalizePlanPresets(store.config.plan_presets);
        cfg.plan_presets = cfg.plan_presets.map((plan) => {
          if (![API_KEY_MASK, LEGACY_API_KEY_MASK, HOST_API_KEY_MASK].includes(String(plan.api_key || '').trim())) {
            return plan;
          }
          const existing = previousPlans.find((item) => item.id === plan.id);
          return { ...plan, api_key: existing ? existing.api_key : '' };
        });
        store = {
          config: cfg,
          roster: normalizeRoster(parsed.roster),
          state: normalizeState(parsed.state)
        };
        selectedRosterId = null;
        renderAll();
        persistLocalDebugPrefs('import_snapshot', { quiet: true });
        setBackendConfigDirtyState(true, '快照已导入，正式配置正在同步到 FastAPI。');
        queueBackendSettingsAutoSave('import_snapshot', { immediate: true });
        applyRosterImmediate('import_snapshot', { quiet: true });
        setStatus('ok', '快照导入成功。');
        log('OK', '快照导入成功。');
      } catch (e) {
        const message = e instanceof Error ? e.message : String(e);
        setStatus('err', `快照导入失败：${message}`);
        log('ERROR', '快照导入失败。', { message });
      }
    }

    function resetAll() {
      store = {
        config: normalizeConfig(DEFAULT_CONFIG),
        roster: normalizeRoster(DEFAULT_ROSTER),
        state: normalizeState(DEFAULT_STATE)
      };
      selectedRosterId = null;
      $('snapshot').value = '';
      $('instruction').value = '';
      $('runDetail').value = '';
      $('nMsg').value = '';
      $('noticeList').innerHTML = '';
      renderAll();
      persistLocalDebugPrefs('reset_all', { quiet: true });
      setBackendConfigDirtyState(true, '已重置为默认测试数据，正式配置正在同步到 FastAPI。');
      queueBackendSettingsAutoSave('reset_all', { immediate: true });
      applyRosterImmediate('reset_all', { quiet: true });
      setStatus('ok', '已重置为默认测试数据。');
      log('OK', '已重置为默认测试数据。');
    }

    function applyHost(data) {
      if (!data || typeof data !== 'object') {
        log('WARN', '收到无效宿主消息。', data);
        return;
      }
      if (data.type === 'host_theme' && data.payload) {
        applyHostTheme(data.payload);
      }
      if (data.type === 'error') {
        log('ERROR', `宿主返回错误：${data.code || '未知错误'}`, data);
      }
    }

    function bindConfigAutoApply() {
      const backendFieldIds = [
        'cfgPlanName', 'cfgPlanMode', 'cfgApi', 'cfgBase', 'cfgModel',
        'cfgProfile', 'cfgProvider', 'cfgMulti', 'cfgRule'
      ];
      for (const id of backendFieldIds) {
        const el = $(id);
        if (!el) continue;
        const tag = el.tagName.toLowerCase();
        if (tag === 'select') {
          el.addEventListener('change', () => {
            queueBackendSettingsAutoSave(`change:${id}`);
          });
          continue;
        }
        if (tag === 'textarea') {
          el.addEventListener('blur', () => {
            queueBackendSettingsAutoSave(`blur:${id}`);
          });
          el.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault();
              queueBackendSettingsAutoSave(`hotkey:${id}`, { immediate: true });
            }
          });
          continue;
        }
        el.addEventListener('blur', () => {
          queueBackendSettingsAutoSave(`blur:${id}`);
        });
      }

      $('cfgPlan').addEventListener('focus', () => {
        $('cfgPlan').dataset.currentId = store.config.selected_plan_id;
      });
      $('cfgPlan').addEventListener('change', () => {
        syncConfigFromForm();
        store.config = normalizeConfig({
          ...store.config,
          selected_plan_id: $('cfgPlan').value
        });
        renderConfig();
        queueBackendSettingsAutoSave('change:cfgPlan');
      });

      $('cfgPer').addEventListener('blur', () => {
        applyLocalConfigImmediate('blur:cfgPer', { quiet: true });
      });
      $('cfgPer').addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          applyLocalConfigImmediate('enter:cfgPer', { quiet: true });
        }
      });
    }

    function bind() {
      $('mode').addEventListener('change', () => setMode($('mode').value));
      $('saveConfigBtn').addEventListener('click', () => { void flushBackendSettingsAutoSave('manual_save'); });
      $('reloadConfigBtn').addEventListener('click', () => { void loadSettingsFromBackend('manual_reload'); });
      $('runBtn').addEventListener('click', () => runtimeMode === 'backend' ? void runBackend() : runMock());
      $('instruction').addEventListener('keydown', (event) => {
        if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
          event.preventDefault();
          runtimeMode === 'backend' ? void runBackend() : runMock();
        }
      });
      $('loadBtn').addEventListener('click', () => {
        log('INFO', '触发加载数据。', { mode: runtimeMode });
        if (runtimeMode === 'backend') {
          void Promise.all([
            loadSettingsFromBackend('manual_refresh'),
            loadSnapshotFromBackend('manual_refresh')
          ]);
        } else {
          renderAll();
          setStatus('ok', '本地模拟数据已刷新。');
          log('OK', '本地模拟数据已刷新。');
        }
      });

      $('addTpl').addEventListener('click', addTpl);
      $('delTpl').addEventListener('click', delTpl);
      $('addStudent').addEventListener('click', addStudent);
      $('delStudent').addEventListener('click', delStudent);
      $('newTpl').addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          addTpl();
        }
      });
      $('newStudent').addEventListener('keydown', (event) => {
        if (event.key === 'Enter') {
          event.preventDefault();
          addStudent();
        }
      });
      bindConfigAutoApply();

      $('previewNotice').addEventListener('click', () => {
        notifyByTemplates($('nScene').value, $('nStatus').value === 'success', $('nMsg').value.trim(), $('instruction').value.trim(), $('applyMode').value, clamp(int($('nDuration').value, 7), 1, 30), false);
        setStatus('ok', '通知模板预览已更新。');
        log('OK', '通知模板预览完成。');
      });

      $('sendNotice').addEventListener('click', () => {
        notifyByTemplates($('nScene').value, $('nStatus').value === 'success', $('nMsg').value.trim(), $('instruction').value.trim(), $('applyMode').value, clamp(int($('nDuration').value, 7), 1, 30), true);
        if (hasBridge) setStatus('warn', '通知已发送到宿主。');
        else setStatus('ok', '宿主不可用，仅本地预览。');
      });
      $('triggerRunCompletionNotice').addEventListener('click', triggerRunCompletionNotification);
      $('triggerDutyReminderNotice').addEventListener('click', triggerDutyReminderNotification);
      $('openInBrowserBtn').addEventListener('click', openTestInBrowser);

      $('exportBtn').addEventListener('click', exportSnapshot);
      $('importBtn').addEventListener('click', importSnapshot);
      $('resetBtn').addEventListener('click', resetAll);
      $('clearLogBtn').addEventListener('click', () => {
        clearLogs();
        setStatus('ok', '日志已清空。');
      });
      $('errorLogBtn').addEventListener('click', toggleErrorLogFilter);

      if (hasBridge) {
        window.chrome.webview.addEventListener('message', (event) => {
          log('RECV', '接收到宿主消息。', event.data);
          applyHost(event.data);
        });
      }
    }

    bind();
    setMode(hasBackendTransport ? 'backend' : 'mock');
    renderAll();
    requestAnimationFrame(() => document.body.classList.add('page-ready'));
    if (hasBackendTransport) {
      void Promise.all([
        loadSettingsFromBackend('startup'),
        loadSnapshotFromBackend('startup')
      ]);
    }
    setStatus('warn', '测试控制台已就绪。');
    log('INFO', '页面初始化完成。');
  
