/* Pure telemetry-to-scene mapping. No stored conversations, no invented rates. */
(function (root) {
  'use strict';
  const level = (n, limits) => !Number.isFinite(n) || n <= 0 ? 0 : 1 + limits.filter(x => n >= x).length;
  function map(value) {
    const m = value.metrics || {}, last = m.last;
    const sourceOK = value.source?.state === 'ok';
    const current = last && Number.isFinite(m.turn_started_at_ms) &&
      last.measured_at_ms >= m.turn_started_at_ms &&
      (!last.turn_id || !m.turn_id || last.turn_id === m.turn_id);
    const active = sourceOK && m.stage === 'generating';
    const rate = current && Number.isFinite(last.rate) && last.rate > 0 ? last.rate : null;
    return {
      active, idle: sourceOK && m.stage === 'idle',
      new_input_estimate: m.user_input?.text_tokens_estimate ?? null,
      bottles: level(m.user_input?.text_tokens_estimate, [100, 500, 2000, 8000]) || (m.user_input?.attachments_unknown ? 1 : 0),
      lanes: current ? level(m.turn_output_tokens, [500, 2000, 5000, 15000]) : 0,
      // Compress the broad rate range into legible pixels/second, monotonically.
      speed: rate === null ? 0 : Math.min(300, 12 + 26 * Math.log2(1 + rate / 8)),
      rate, current: !!current,
      label: !sourceOK ? '日志暂不可读 · 已暂停' : m.stage === 'idle' ? '对话已结束 · 休息一下' :
        m.stage === 'tools' ? '工具执行中 · 暂停输出' : !current || rate === null ? '正在工作 · 等待本轮 Token 计数' : '正在输出 · 速度依据最近完成响应',
    };
  }
  const api = { level, map };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.CrocModel = api;
})(globalThis);
