'use strict';
const $ = id => document.getElementById(id);
const zone = 'America/New_York';
const eastern = new Intl.DateTimeFormat('sv-SE', {timeZone:zone, dateStyle:'short', timeStyle:'medium'});
const short = new Intl.DateTimeFormat('zh-CN', {timeZone:zone, hour:'2-digit', minute:'2-digit', hourCycle:'h23'});
const date = at => at ? eastern.format(at) : '—';
const number = n => n == null ? '—' : Number(n).toFixed(1);
const wifiNames = {Gongxifacai:'Gongxifacai · Google Nest',Astound:'Astound',switching:'切换中',unknown:'未知',other:'其他 Wi‑Fi',legacy:'旧记录，无网络标签'};
const wifiName = n => wifiNames[n] ?? wifiNames.unknown;
const wifiColors = {Gongxifacai:'#00676d',Astound:'#344f94',switching:'#9a5932',legacy:'#586d78',unknown:'#586d78',other:'#4d6572'};
const kinds = {'hvac-start':'HVAC 启动','hvac-stop':'HVAC 停止',equipment:'设备变更',ups:'UPS 安装',note:'其他'};
const diagnostics = {wifi:'此电脑的 Wi‑Fi 接口断开',local:'本地接入链路异常',icmp:'网关 ICMP 响应异常',dns:'配置的 DNS 可能异常',modem:'Modem 或管理路径可能异常',isp:'ISP 接入路径可能异常',wan:'宽带侧可能异常','modem-status':'Modem 状态探测失败',external:'单个外网目标异常',unclear:'探测证据不足',latency:'探测延迟持续偏高'};
const evidenceCopy = {
  wifi:['Windows 报告 Wi-Fi 接口已断开。','范围包括电脑网卡、接入点或无线链路。','此电脑的 Wi-Fi 接口已确认断开。'],
  local:['网关及其后的探测均未收到响应。','可能涉及电脑 Wi-Fi、Mesh 链路或网关。'],
  icmp:['网关未回应 ICMP，下游仍有目标回应。','可能是网关对 ICMP 的处理方式。'],
  dns:['配置的 DNS 失败，公共对照解析器和直连 TCP 均有响应。','可能涉及配置的解析器或其转发路径。'],
  modem:['网关有响应，多个外网目标和配置的 modem 探测失败。','可能涉及 modem、管理路径或与 Mesh 的连接。'],
  isp:['网关和上游设备有响应，ISP 参考地址及多个公共目标失败。','可能涉及 modem 至 ISP 的接入路径。'],
  wan:['网关有响应，多个外网目标在 ICMP 和 TCP 或 HTTPS 上失败。','可能涉及宽带链路或上游网络。'],
  'modem-status':['配置的 modem 探测失败。','可能涉及设备的管理路径。DOCSIS 状态待核对。'],
  external:['单个外网服务或 TCP 目标失败，其他公共 TCP 目标有响应。','可能涉及该服务或它的路径。'],
  latency:['探测延迟连续超过阈值。','可能涉及排队、无线干扰或路径拥塞。']
};
const speeds = {running:'进行中',ok:'完成',failed:'失败',timeout:'超时',interrupted:'中断'};
const gaps = {'service-not-running':'服务未运行','storage-unavailable':'存储不可用','host-suspended-or-collector-delayed':'主机休眠或采集延迟'};
const speedErrors = {disabled:'测速尚未启用','already-running':'已有测速正在运行','connectivity-unavailable':'外网或 DNS 当前异常','waiting-for-idle':'等待这台电脑的网络空闲','server-not-configured':'测速程序或服务器文件尚未配置','invalid-server':'测速服务器配置无效'};
let history = {points:[],series:[],annotations:[],incidents:[],gaps:[],speeds:[],wifi:[],nest:{heating:[],gaps:[]}};
let latest = [], currentRange = [0,0], incidentKey = '', historyRequest = 0, speedMessage = '';
const chart = $('chart');
const minute = 60000;
let zoomRange = null, zoomBase = null, drag = null;
const plotBounds = (width,height) => ({left:45,right:width-12,top:14,bottom:height-32});
const node = (tag, value, cls) => {
  const el = document.createElement(tag);
  if (value != null) el.textContent = value;
  if (cls) el.className = cls;
  return el;
};
function fill(id, rows, empty) {
  $(id).replaceChildren(...(rows.length ? rows : [node('p',empty)]));
}
async function api(url, body) {
  const response = await fetch(url, body == null ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  if (!response.ok) {
    const info = await response.json().catch(() => ({}));
    throw new Error(info.error ?? String(response.status));
  }
  return response.status === 202 ? null : response.json();
}
function options() {
  const selected = $('series').value;
  const known = new Map([...latest, ...history.series].map(s => [s.id + '/' + s.protocol, s.label + ' · ' + s.protocol.toUpperCase()]));
  $('series').replaceChildren(...Array.from(known, ([id,label]) => new Option(label,id)));
  if (known.has(selected)) $('series').value = selected;
  else if (known.has('gateway/icmp')) $('series').value = 'gateway/icmp';
}
async function status() {
  try {
    const state = await api('/api/status');
    const {live} = state;
    latest = live.samples;
    const lag = !live.at || Date.now() - live.at > state.pingSeconds * 3500;
    const kind = live.active?.diagnosis.kind ?? live.evidence?.kind;
    const switching = live.network === 'switching';
    $('status-title').textContent = lag ? '等待新读数' : live.storageError ? '存储暂不可用' : switching ? '正在切换 Wi‑Fi' : kind ? diagnostics[kind] : '探测正常';
    $('status-detail').textContent = lag ? '' : live.storageError ? '正在继续探测，历史暂时无法写入。' :
      switching ? '此轮探测标记为切换中。' : live.active ? '已记录持续异常，可展开时间线查看证据。' : kind ? '正在复查，单次探测结果已保留。' : '';
    $('lamp').className = lag ? '' : kind || live.storageError ? 'bad' : 'ok';
    $('heartbeat').textContent = date(live.at);
    const link = live.link;
    const ssid = link?.ssid === 'Gongxifacai' ? wifiName(link.ssid) : link?.ssid ?? 'Wi‑Fi 未知';
    $('link').textContent = link?.known ? [link.connected ? ssid : 'Wi‑Fi 断开',link.gateway, link.signalDbm == null ? '信号不可用' : link.signalDbm + ' dBm'].filter(Boolean).join(' / ') : 'Wi‑Fi 网卡不可用';
    const {nest} = state;
    const nestCopy = {disabled:'自动记录未启用。',starting:'正在启动 Nest 采集。',connecting:'正在连接事件订阅。',
      'setup-required':'等待 Nest 授权和订阅配置。','feed-unavailable':'Nest 事件暂时不可用。','thermostat-offline':'恒温器离线。',connected:'已接收 Nest 状态。'};
    const hvac = {HEATING:'供暖中',COOLING:'制冷中',OFF:'空闲'};
    $('nest-status').textContent = [nestCopy[nest?.status] ?? '正在读取状态。',hvac[nest?.hvac],nest?.lastContact ? '最近联系 ' + date(nest.lastContact) : null].filter(Boolean).join('　');
    $('targets').replaceChildren(...latest.map(s => {
      const card = node('div',null,'target');
      card.append(node('span',s.label,'name'),node('small',s.protocol.toUpperCase()),node('span',
        s.ok ? number(s.ms) + ' ms' : ({timeout:'超时',failure:'失败',unavailable:'不可用'})[s.outcome], s.ok ? 'value' : 'value bad'));
      if (s.detail) card.title = s.detail;
      return card;
    }));
    $('speed-run').disabled = !state.speedEnabled || state.speedRunning;
    $('speed-msg').textContent = state.speedRunning ? '正在测速，探测图表会标出负载。' :
      !state.speedEnabled ? '已关闭。配置测速服务器后可在配置文件中启用。' : speedMessage;
    if (!$('series').options.length) { options(); draw(); }
  } catch {
    $('status-title').textContent = '本机服务连接失败';
    $('lamp').className = 'bad';
    $('speed-run').disabled = true;
  }
}
function query() {
  const day = document.getElementById('ending').value;
  let to = Date.now() + 1;
  if (day) {
    const next = new Date(Date.parse(day + 'T00:00:00Z') + 86400000).toISOString().slice(0,10);
    for (const offset of ['-04:00','-05:00']) {
      const candidate = Date.parse(next + 'T00:00:00' + offset);
      if (eastern.format(candidate).startsWith(next + ' 00:00')) to = Math.min(to,candidate);
    }
  }
  return [to - Number($('range').value) * 3600000,to];
}
async function refresh() {
  const request = ++historyRequest;
  const range = zoomBase ?? query();
  const filter = $('network').value;
  const params = '?from=' + range[0] + '&to=' + range[1] + (filter ? '&network=' + encodeURIComponent(filter) : '');
  const [results, correlation] = await Promise.allSettled([api('/api/history' + params + '&series=' + encodeURIComponent(document.getElementById('series').value || 'gateway/icmp')),api('/api/correlation' + params)]);
  if (request !== historyRequest) return;
  if (results.status === 'rejected') { $('chart-note').textContent = '历史暂不可用。'; return; }
  currentRange = range;
  history = results.value;
  showWifiComparison(history.comparison);
  showHeating(history.nest);
  const changeText = {initial:'开始观测','connection-changed':'连接状态变化','during-round':'探测期间发生切换'};
  fill('wifi-changes',history.wifi.slice(0,30).map(w => node('p',date(w.at) + '　' + wifiName(w.previous) + ' → ' + wifiName(w.current) + '　' + changeText[w.reason],'entry')), '所选时段内无切换记录。');
  options();
  draw();
  const key = JSON.stringify(history.incidents);
  if (incidentKey !== key) {
    incidentKey = key;
    fill('incidents',history.incidents.map(x => {
      const item = node('details');
      item.append(node('summary',wifiName(x.network) + '　' + diagnostics[x.diagnosis.kind] + '　' + date(x.start) + (x.end ? ' – ' + date(x.end) : ' · 持续中')));
      const copy = evidenceCopy[x.diagnosis.kind];
      const unclear = x.diagnosis.observation.startsWith('No usable')
        ? ['没有可用的 IPv4 网关读数。','可能涉及 Wi-Fi 关联、地址分配或路由。']
        : ['外网协议探测的结果不一致。','可能涉及协议过滤、DNS 或部分链路异常。'];
      const lines = copy ?? unclear;
      item.append(node('p',lines[0]),node('p',lines[1]),node('p',lines[2] ?? '原因待确认。'));
      const evidence = node('div');
      item.append(evidence);
      item.addEventListener('toggle',async () => {
        if (!item.open) return;
        try {
          const captures = await api('/api/incident/' + x.id);
          evidence.replaceChildren(...captures.map(s => node('pre',date(s.at) + '\n' + (s.link.ssid ?? 'Wi-Fi') + ' ' + (s.link.signalDbm ?? '—') + ' dBm\n' + [...s.wlan,s.trace].filter(Boolean).join('\n'))));
          if (!captures.length) evidence.replaceChildren(node('p','正在等待现场快照。'));
        } catch { evidence.replaceChildren(node('p','现场快照暂不可用。')); }
      });
      return item;
    }),'所选时段内没有持续故障。');
  }
  fill('gaps',history.gaps.map(x => node('p',date(x.start) + ' – ' + date(x.end) + '　采集空档 · ' + (gaps[x.reason] ?? x.reason),'warning')), '');
  fill('annotations',history.annotations.map(a => node('p',date(a.at) + '　' + kinds[a.kind] + (a.note ? '　' + a.note : ''),'entry')), '所选时段内没有事件。');
  fill('speeds',history.speeds.map(s => node('p',date(s.start) + '　' + wifiName(s.network) + '　' + speeds[s.status] +
     (s.download == null ? '' : '　下载 ' + number(s.download) + ' / 上传 ' + number(s.upload) + ' Mbps'),'entry')), '所选时段内没有测速。');
  if (correlation.status === 'fulfilled') showCorrelation(correlation.value);
  else fill('correlation',[],'暂无法读取关联数据。');
  const exports = {'/export/samples.csv':'原始样本 CSV','/export/minutes.csv':'分钟汇总 CSV','/export/incidents.csv':'故障 CSV','/export/gaps.csv':'空档 CSV','/export/annotations.csv':'事件 CSV','/export/speeds.csv':'测速 CSV','/export/wifi.csv':'切换 CSV','/export/heating.csv':'供暖 CSV','/report':'英文 ISP 报告'};
  $('exports').replaceChildren(...Object.entries(exports).map(([path,label]) => {
    const link = node('a',label); link.href = path + params; return link;
  }));
}
function showWifiComparison(rows) {
  const table = node('table',null,'wifi-compare');
  const header = table.createTHead().insertRow();
  for (const title of ['Wi‑Fi','观测时长','外网 Ping 次数','外网丢包','外网平均延迟','网关丢包','本地 DNS 失败']) {
    const th = node('th',title); th.scope = 'col'; header.append(th);
  }
  const pct = n => n == null ? '—' : number(n) + '%';
  const body = table.createTBody();
  for (const row of rows) {
    const tr = body.insertRow();
    const duration = row.observedSeconds >= 60 ? number(row.observedSeconds/60) + ' 分钟' : row.observedSeconds + ' 秒';
    for (const value of [wifiName(row.network),duration,row.publicAttempts,pct(row.publicLoss),row.publicRtt == null ? '—' : number(row.publicRtt) + ' ms',pct(row.gatewayLoss),pct(row.dnsFailure) + ' / ' + row.dnsAttempts + ' 次'])
      tr.insertCell().textContent = value;
  }
  $('wifi-comparison').replaceChildren(table);
}
function showHeating(nest) {
  fill('heating',nest.heating.slice().reverse().map(s => node('p',
    (s.start ? date(s.start) : '开始时间未知，首次观察 ' + date(s.observedFrom)) + ' → ' +
    (s.end ? date(s.end) : s.active ? '供暖中' : '结束时间未知'),'entry')), '所选时段内无 Nest 供暖记录。');
  const cause = {'event-feed':'事件订阅暂不可用','thermostat-offline':'恒温器离线'};
  fill('nest-gaps',nest.gaps.map(g => node('p',date(g.start) + ' – ' + (g.end ? date(g.end) : '持续中') + '　' + cause[g.reason],'warning')),'');
}
function showCorrelation(rows) {
  const metrics = (points, id, protocol) => points.find(p => p.id === id && p.protocol === protocol);
  const fmt = p => p?.attempts ? number(p.loss) + '% / ' + number(p.rtt) + ' ms / ' + p.attempts : '—';
  const result = rows.map(row => {
    const article = node('details');
    article.append(node('summary',date(row.event.at) + '　' + wifiName(row.network) + '　' + kinds[row.event.kind] + (row.event.source === 'nest' ? ' · Nest 自动记录' : '')));
    const scroll = node('div',null,'scroll');
    const table = node('table');
    const header = table.insertRow();
    for (const value of ['目标','事件前','事件后','对照']) header.append(node('th',value));
    for (const p of new Map([...row.before,...row.after,...row.control].map(x => [x.id+'/'+x.protocol,x])).values()) {
      const tr = table.insertRow();
      tr.append(node('th',p.label + ' / ' + p.protocol));
      for (const key of ['before','after','control']) tr.insertCell().textContent = fmt(metrics(row[key],p.id,p.protocol));
    }
    scroll.append(table);
    article.append(scroll,node('p','失败率 / 平均延迟 / 尝试数。覆盖率 ' + [row.beforeCoverage,row.afterCoverage,row.controlCoverage].map(n => number(n) + '%').join(' / ')));
    return article;
  });
  for (const wifi of ['Gongxifacai','Astound','legacy']) {
    const starts = rows.filter(x => x.network === wifi && x.event.kind === 'hvac-start' && x.afterCoverage > 0 && x.controlCoverage > 0);
    if (starts.length < 2) continue;
    const format = key => {
      const points = starts.flatMap(r => r[key]).filter(p => p.layer === 'public' && p.protocol === 'icmp');
      const attempts = points.reduce((sum,p) => sum + p.attempts,0);
      const ok = points.reduce((sum,p) => sum + p.successes,0);
      return attempts ? number(100*(attempts-ok)/attempts) + '% / ' + attempts + ' 次' : '无样本';
    };
    result.unshift(node('p',wifiName(wifi) + '　' + starts.length + ' 次 HVAC 启动的公共 IP 探测失败率。启动后 ' + format('after') + '，对照 ' + format('control') + '。按事件汇总，重叠窗口可重复计入。'));
  }
  fill('correlation',result,'记录 HVAC 启停后可查看相邻时段。');
}
function draw() {
  const canvas = chart;
  const {width, height} = canvas.getBoundingClientRect();
  if (!width || !height) return;
  const dpr = devicePixelRatio || 1;
  canvas.width = Math.round(width*dpr); canvas.height = Math.round(height*dpr);
  const context = canvas.getContext('2d'); context.scale(dpr,dpr);
  const field = $('metric').value;
  const [from,to] = drag?.range ?? zoomRange ?? currentRange;
  const plotTime = point => Math.max(currentRange[0],Math.min(currentRange[1],point.at + minute/2));
  const points = history.points.filter(p => p.id + '/' + p.protocol === $('series').value &&
    plotTime(p) >= from && plotTime(p) <= to);
  const values = points.filter(p => p[field] != null);
  const max = field === 'loss' ? 100 : Math.max(5,Math.ceil(Math.max(0,...values.map(p => p[field])) / 5)*5);
  const {left,right,top,bottom} = plotBounds(width,height);
  const x = at => left + (right-left) * Math.max(0,Math.min(1,(at-from)/(to-from || 1)));
  const y = value => bottom - (bottom-top)*value/max;
  context.font = '12px Segoe UI'; context.fillStyle = '#48616b'; context.strokeStyle = '#d7e5e6';
  for (let i=0;i<=4;i++) {
    const value = max*i/4;
    context.beginPath(); context.moveTo(left,y(value)); context.lineTo(right,y(value)); context.stroke();
    context.fillText(Math.round(value)+(field==='loss'?'%':''),4,y(value)+4);
  }
  if (from) {
    context.fillText(short.format(from),left,bottom+21);
    context.textAlign = 'right'; context.fillText(short.format(to),right,bottom+21); context.textAlign = 'left';
  }
  for (const gap of history.gaps) {
    if (gap.end < from || gap.start > to) continue;
    context.fillStyle = '#dfe7ea'; context.fillRect(x(gap.start),top,Math.max(1,x(gap.end)-x(gap.start)),bottom-top);
  }
  for (const speed of history.speeds) {
    if ((speed.end ?? speed.start+45000) < from || speed.start > to) continue;
    context.fillStyle = '#e2ce9d'; context.fillRect(x(speed.start),top,Math.max(2,x(speed.end ?? speed.start+45000)-x(speed.start)),bottom-top);
  }
  for (const span of history.nest.heating) {
    if ((span.end ?? span.observedTo) < from || (span.start ?? span.observedFrom) > to) continue;
    context.fillStyle = '#f5d9b5';
    const start = x(span.start ?? span.observedFrom);
    context.fillRect(start,top,Math.max(2,x(span.end ?? span.observedTo)-start),bottom-top);
  }
  for (const change of history.wifi) {
    if (change.at < from || change.at > to) continue;
    context.strokeStyle = '#8d7772'; context.setLineDash([3,3]); context.beginPath();
    context.moveTo(x(change.at),top); context.lineTo(x(change.at),bottom); context.stroke();
    context.setLineDash([]);
  }
  for (const a of history.annotations) {
    if (a.at < from || a.at > to) continue;
    context.strokeStyle = '#9a5932'; context.beginPath(); context.moveTo(x(a.at),top); context.lineTo(x(a.at),bottom); context.stroke();
  }
  const grouped = new Map();
  for (const p of values) {
    if (!grouped.has(p.network)) grouped.set(p.network,[]);
    grouped.get(p.network).push(p);
  }
  for (const [wifi, series] of grouped) {
    context.strokeStyle = context.fillStyle = wifiColors[wifi] ?? wifiColors.unknown;
    context.lineWidth = 1.6;
    let previous;
    for (const p of series) {
      const px = x(plotTime(p)), py = y(p[field]);
      if (previous && p.at-previous.at === 60000 && !history.wifi.some(w => w.at > previous.at && w.at < p.at + 60000)) {
        context.beginPath(); context.moveTo(x(plotTime(previous)),y(previous[field])); context.lineTo(px,py); context.stroke();
      }
      context.fillRect(px-1,py-1,2,2);
      previous = p;
    }
  }
  const legend = [...grouped.keys()].map(w => [wifiName(w),w === 'Gongxifacai' ? 'nest' : w === 'Astound' ? 'astound' : w === 'switching' ? 'switching' : 'gap']);
  if (history.nest.heating.length) legend.push(['Nest 供暖','heating']);
  if (history.wifi.length) legend.push(['虚线为 Wi‑Fi 切换','gap']);
  $('chart-legend').replaceChildren(...legend.map(([label,cls]) => node('span',label,'legend-item ' + cls)));
  if (drag) {
    const start = Math.min(drag.start,drag.end);
    const size = Math.abs(drag.end-drag.start);
    context.fillStyle = 'rgba(0,103,109,.18)';
    context.fillRect(start,top,size,bottom-top);
    context.strokeStyle = '#00676d';
    context.lineWidth = 1;
    context.strokeRect(start,top,size,bottom-top);
  }
  $('chart-zoom-in').disabled = to-from <= 2*minute;
  $('chart-zoom-reset').disabled = !zoomRange;
  const view = $('chart-view');
  view.hidden = !zoomRange;
  const viewText = zoomRange ? '已放大 · ' + date(from) + ' 至 ' + date(to) + '（纽约时间）' : '';
  if (view.textContent !== viewText) view.textContent = viewText;
  const last = values.at(-1);
  $('chart-note').textContent = last ? '图中最后一分钟 · ' + wifiName(last.network) + ' ' + number(last[field]) + (field === 'loss' ? '%':' ms') + '。灰色为采集空档，浅金为测速，棕线为家庭事件。' : '此目标在所选范围暂无可绘制样本。';
}

function zoom(from,to,range) {
  const [first,last] = range;
  const size = Math.min(last-first,Math.max(2*minute,to-from));
  if (size >= last-first) { draw(); return; }
  const start = Math.max(first,Math.min(last-size,(from+to-size)/2));
  zoomBase ??= currentRange;
  zoomRange = [start,start+size];
  draw();
}
function clearZoom() {
  zoomRange = zoomBase = drag = null;
  draw();
  refresh();
}
function pointerX(event) {
  const rect = chart.getBoundingClientRect();
  const {left,right} = plotBounds(rect.width,rect.height);
  return Math.max(left,Math.min(right,event.clientX-rect.left));
}
chart.addEventListener('pointerdown',event => {
  const rect = chart.getBoundingClientRect();
  const {left,right,top,bottom} = plotBounds(rect.width,rect.height);
  const x = event.clientX-rect.left, y = event.clientY-rect.top;
  const range = zoomRange ?? currentRange;
  if (!event.isPrimary || event.button !== 0 || x < left || x > right || y < top || y > bottom || range[1]-range[0] <= 2*minute) return;
  drag = {id:event.pointerId,start:x,end:x,range};
  chart.setPointerCapture(event.pointerId);
});
chart.addEventListener('pointermove',event => {
  if (drag?.id !== event.pointerId) return;
  drag.end = pointerX(event);
  draw();
});
chart.addEventListener('pointerup',event => {
  if (drag?.id !== event.pointerId) return;
  const {start,range} = drag;
  const end = pointerX(event);
  drag = null;
  if (Math.abs(end-start) < 8) { draw(); return; }
  const {left,right} = plotBounds(chart.clientWidth,chart.clientHeight);
  const time = x => range[0]+(x-left)/(right-left)*(range[1]-range[0]);
  zoom(time(Math.min(start,end)),time(Math.max(start,end)),range);
});
chart.addEventListener('lostpointercapture',event => {
  if (drag?.id !== event.pointerId) return;
  drag = null;
  draw();
});
$('chart-zoom-in').addEventListener('click',() => {
  const range = zoomRange ?? currentRange;
  const quarter = (range[1]-range[0])/4;
  zoom(range[0]+quarter,range[1]-quarter,range);
});
$('chart-zoom-reset').addEventListener('click',clearZoom);
const form = $('annotation');
function setTime() { form.elements.when.value = eastern.format(Date.now()).slice(0,16).replace(' ','T'); }
form.addEventListener('submit',async event => {
  event.preventDefault();
  const value = form.elements.when.value;
  const matches = [-4,-5].filter(offset => !form.elements.offset.value || +form.elements.offset.value === offset)
    .map(offset => Date.parse(value + (offset === -4 ? '-04:00':'-05:00')))
    .filter(at => Number.isFinite(at) && eastern.format(at).slice(0,16).replace(' ','T') === value);
  if (matches.length !== 1) {
    $('annotation-msg').textContent = matches.length ? '此时间出现两次，请指定 UTC 偏移。' : '纽约当地时间或 UTC 偏移无效。'; return;
  }
  try {
    await api('/api/annotations',{kind:form.elements.kind.value,at:matches[0],note:form.elements.note.value});
    form.elements.note.value=''; setTime(); $('annotation-msg').textContent='已保存。'; await refresh();
  } catch { $('annotation-msg').textContent='未能保存。请核对时间范围和存储状态。'; }
});
$('speed-run').addEventListener('click', async () => {
  $('speed-run').disabled = true;
  try { await api('/api/speed/run',{}); speedMessage='已启动测速。'; }
  catch (error) { speedMessage=speedErrors[error.message] ?? '未能启动测速。'; }
  await status(); await refresh();
});
$('range').addEventListener('change',clearZoom);
$('ending').max = eastern.format(Date.now()).slice(0,10);
$('ending').addEventListener('change',clearZoom);
$('series').addEventListener('change',refresh);
$('network').addEventListener('change',refresh);
$('metric').addEventListener('change',draw);
new ResizeObserver(draw).observe($('chart'));
setTime(); status(); refresh();
setInterval(status,5000); setInterval(refresh,30000);
