// 零构建：原生 JS，无打包、无依赖、无类型。页面直连核心（WS + HTTP），头不代理。
// 头是可替换件——履行义务即可，其余是实现自由。
//
// ⚠️ 按 `05` §4 四条义务逐条对（`05` §11 引这里当判据——⚠️ **改义务状态时改这里，别删**）：
//   ① 两个槽位原样显示 —— 做了（并排、常驻、空的也显示。⚠️ 不许改成折叠：归位错误只在并排时看得出）
//   ② 声明可见         —— 做了（保守：只做「声明主动」那一格；冲突顶到右栏）
//   ③ 审批走独立流     —— 阻塞：核心侧闸门不存在，页面明说而不是显示空壳
//   ④ 安全轴显眼       —— 做了（右栏最上 + 跨轴告警）
//
// ---- 本版着重三块：提供商 / 会话 / 项目 ----------------------------------
// 其余（审批、附件、控制点、工具）不实现，只留结构位置（右栏 .panel + 底部状态栏）。
//
// ⚠️ 本文件**不许出现轴名字面量**（"safety" / "permission"）：
//   轴表、标签、告警线的两端**全部从 `GET /axes` 来**（G8：单一真相来源是核心的 `Axes.Table`）。
//   2026-10-05 之前这里有 `"safety"` 与两处 `"permission"` 手抄副本 —— 改核心轴名会让
//   头**静默**不画安全轴、不报警（判据恒 false）。现在一律 `AXES.find(...)` / `AXES.some(...)`。

const $ = (id) => document.getElementById(id);

let core = "http://127.0.0.1:7788";
let ws = null;
let wsReady = false;                // 唯一的「可以发帧了吗」判据（见 connect()）
let current = null;                 // 当前会话 id（视图，非真相）
let epoch = 0;                      // 每次 openSession 自增，用来区分同 id 的并发重入
let creating = null;                // 刚发出的 session.create 的命令 id
let currentProject = null;
const sessions = new Map();         // sessionId -> { projectKey }（派生索引，次序 = 显示次序）
const cursors = new Map();          // topic -> lastSeq，每流一个游标
const claims = new Map();           // sessionId -> Set<path>（声明）
let axes = {};                      // 当前会话的轴值

// 派生视图缓存：不发事件的东西靠它渲染（项目名 / 步数 / 状态）。
const projects = new Map();         // pk -> { dir?, count }
const info = new Map();             // sid -> { spec?, turn, status }
let providers = [];
const provNotes = new Map();        // providerId -> { text, ok }（最近一次自检的结果）
const testCmd = new Map();          // 命令 id -> providerId（回执认得回来）

// ---- 引导：核心启动后没有会话与提供商，「配置提供商 → 建初始会话」是头的策略 ----
// bootstrap() 有两个入口（首屏 IIFE 与 onopen 自恢复）且不可重入 ⇒ 用一次性承诺去重；
// 失败时清空，让核心起来后的 onopen 能重试。
let bootPromise = null;
function bootstrap() {
  if (bootPromise) return bootPromise;
  bootPromise = runBootstrap().catch((e) => { bootPromise = null; throw e; });
  return bootPromise;
}

async function runBootstrap() {
  // 轴表先拉：AXES 为空则右栏画不出任何控件。
  await loadAxes();
  await loadSessions();
  await loadProviders();
  loadCoreFacts();                  // 底栏是自述，不阻塞引导 —— 失败了也只是底栏空着

  // ① 没有提供商就不能做别的（引导页挡住其余操作）
  if (providers.length === 0) { showOnboarding(); return; }
  hideOnboarding();

  // ② 当前项目下有会话？
  if (sessions.size === 0) {
    // session.create 由头调（核心只认命令）。connect() 返回时握手还没完成，
    // 而 command() 在 !wsReady 时拒绝发送 ⇒ 必须先 await connected()。
    await connected();
    creating = "boot-create";
    command("boot-create", { kind: "session.create" });
    return;
  }

  // ③ 有 ⇒ 打开最近的（不新建）。ListSessions 按 CreatedAt 降序 ⇒ [0] 才是最新，
  // 核心启动时 resume 的也是 [0]。
  openSession([...sessions.keys()][0]);
}

function showOnboarding() {
  $("onboarding").hidden = false;
  $("text").disabled = true;
  $("send").disabled = true;
}

function hideOnboarding() {
  $("onboarding").hidden = true;
  $("text").disabled = false;
  $("send").disabled = false;
}

// ---- 会话：列表是「派生」的（05 §6.1） ----------------------------------
// 全局会话列表 = 对 GET /projects 的每个 pk 取 sessions 的并集；分组名从 spec.workingDirectory 反推。

async function loadSessions() {
  const pks = await (await fetch(core + "/projects")).json();
  sessions.clear();
  projects.clear();
  for (const pk of pks) {
    if (currentProject === null) currentProject = pk;
    const ids = await (await fetch(`${core}/projects/${pk}/sessions`)).json();
    projects.set(pk, { count: ids.length });
    for (const id of ids) sessions.set(id, { projectKey: pk });
  }

  // 项目名 = 该会话的工作目录。只对「还没知道」的会话各拉一次。
  for (const id of sessions.keys()) {
    if (info.has(id) && info.get(id).spec) continue;
    try {
      const st = await (await fetch(`${core}/sessions/${id}/state`)).json();
      info.set(id, {
        spec: st.spec,
        turn: st.state.turn,
        status: st.state.status,
      });
      const p = projects.get(st.spec.projectKey);
      if (p && !p.dir) p.dir = st.spec.workingDirectory;
    } catch { /* 单个会话读不出来不该拖垮整张列表 */ }
  }

  renderSessions();

  // 在这里补订项目流。重复订阅是安全的：核心对同 topic 换掉旧的，
  // 且 since: null 只要从现在起的事件 ⇒ 任一顺序下恰剩一条有效订阅。
  if (currentProject) subscribe(`project:${currentProject}`, null);
}

// 目录名当项目名（完整路径放在 title 里 —— 本机绝对路径不该占一整行）。
function dirName(p) {
  if (!p) return "";
  const m = String(p).replace(/[\\/]+$/, "").match(/[^\\/]+$/);
  return m ? m[0] : String(p);
}

// ISO 时间戳 → 本机短格式。⚠️ 不截字符串：那只是**文本**，与用户所在的时区无关
//（核心交的是 UTC 的 "+00:00"）。先解析再说。
function shortTime(iso) {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return String(iso);
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function renderSessions() {
  const box = $("sessions");
  box.innerHTML = "";
  if (sessions.size === 0) {
    box.innerHTML = '<div class="muted" style="padding:8px 0">还没有会话</div>';
    return;
  }

  // 一把尺：所有刻度按同一最大值算，长短才可比。
  let maxTurn = 1;
  for (const v of info.values()) maxTurn = Math.max(maxTurn, v.turn || 0);

  const groups = new Map();
  for (const [id, s] of sessions) {
    if (!groups.has(s.projectKey)) groups.set(s.projectKey, []);
    groups.get(s.projectKey).push(id);
  }

  for (const [pk, ids] of groups) {
    const meta = projects.get(pk) || {};
    const g = document.createElement("div");
    g.className = "pgroup";

    const head = document.createElement("div");
    head.className = "pgroup-head";
    head.title = meta.dir || pk;
    const nm = document.createElement("span");
    nm.className = "pname";
    nm.textContent = meta.dir ? dirName(meta.dir) : "…";
    const key = document.createElement("span");
    key.className = "pkey";
    key.textContent = pk.slice(0, 8);
    const cnt = document.createElement("span");
    cnt.className = "pcount";
    cnt.textContent = ids.length;
    head.append(nm, key, cnt);
    g.appendChild(head);

    for (const id of ids) {
      const v = info.get(id) || {};

      const row = document.createElement("div");
      row.className = "srow" + (id === current ? " active" : "");
      row.title = id + (v.status ? ` · ${v.status}` : "");

      // 刻度：横杠的高度 = 这一步数 / 本栏最大步数。
      const gauge = document.createElement("span");
      gauge.className = "gauge";
      gauge.style.setProperty("--t", v.turn || 0);
      gauge.style.setProperty("--max", maxTurn);

      const sid = document.createElement("span");
      sid.className = "sid";
      sid.textContent = id.slice(0, 8);

      const turn = document.createElement("span");
      turn.className = "sturn";
      turn.textContent = v.turn === undefined ? "·" : `#${v.turn}`;

      row.append(gauge, sid, turn);
      row.onclick = () => openSession(id);
      g.appendChild(row);
    }
    box.appendChild(g);
  }
}

// ---- 打开会话：先快照，再带高水位订阅 ----

async function openSession(id) {
  // 世代号让后一次调用作废前一次（同 id 双击时 current !== id 判不出来）。
  const gen = ++epoch;

  // subscribe() 在 !wsReady 时会静默返回 ⇒ 先等连接就绪，
  // 否则会话流可能根本没订上，而页面以为订过了。
  await connected();
  if (gen !== epoch) return;
  current = id;
  $("interrupt").disabled = false;   // 没选中会话时它是死的 —— 别让它看起来可点（点了也没事，只是会骗人）
  renderSessions();
  $("title").textContent = id;
  $("title").classList.remove("muted");
  $("timeline").innerHTML = "";

  const state = await (await fetch(`${core}/sessions/${id}/state`)).json();
  const history = await (await fetch(`${core}/sessions/${id}/history`)).json();

  // 快照回来时用户可能已开了别的会话——这些是全局节点，继续渲染会把上一次的
  // turn 追加进已经清空的那条时间线里。
  if (gen !== epoch) return;

  // 派生缓存与面板都更新（供左栏刻度与右栏三块用）。
  info.set(id, { spec: state.spec, turn: state.state.turn, status: state.state.status });
  const p = projects.get(state.spec.projectKey);
  if (p && !p.dir) p.dir = state.spec.workingDirectory;

  renderSlots(state.slots);
  renderAxes(state.axes);
  renderSessionPanel(state);
  renderProjectPanel(state);
  setStatus(state.state.status, state.state.turn);
  renderSessions();
  for (const turn of history.rendered) renderTurn(turn.from, turn.blocks);

  await loadClaims();

  // 又一个 await，再守一次：否则会拿捕获的 id 去订会话流。
  if (gen !== epoch) return;

  // 用快照带回来的高水位当起点：这是「拉快照 → 订阅」窗口不漏不重的唯一办法。
  subscribe(`session:${id}`, history.highWater);
}

function subscribe(topic, since) {
  // 判据是 wsReady，不是 ws.readyState。
  if (!wsReady) return;
  ws.send(JSON.stringify({ kind: "subscribe", topic, since }));
}

// ---- 渲染：核心渲染，头只显示 ----

// ⚠️ 抽文本这一步**不在**核心的读取点里（`SessionQueries` 只有 ModelBody / UserBody），
//    所以这里是**手抄的第二副本**（见 `实现现状.md` §四）。改核心的措辞要同时改这里。
const EMPTY_BODY = "（这一步没有正文输出）";

function textOf(blocks) {
  return (blocks || []).filter((b) => b.kind === "text").map((b) => b.text).join("");
}

function bodyOf(blocks) {
  return textOf(blocks) || EMPTY_BODY;
}

function renderTurn(from, blocks, cls) {
  const el = document.createElement("div");
  el.className = "turn " + (cls || from);

  const who = document.createElement("div");
  who.className = "who";
  who.textContent = from === "user" ? "你" : "⏺";

  const body = document.createElement("div");
  body.className = "body";
  body.textContent = bodyOf(blocks);

  el.append(who, body);
  appendTimeline(el);
}

function renderMeta(text, cls) {
  const el = document.createElement("div");
  el.className = "turn meta " + (cls || "");

  const who = document.createElement("div");
  who.className = "who";
  who.textContent = "·";

  const body = document.createElement("div");
  body.className = "body";
  body.textContent = text;

  el.append(who, body);
  appendTimeline(el);
}

function appendTimeline(el) {
  $("timeline").appendChild(el);
  $("timeline").scrollTop = $("timeline").scrollHeight;
}

function renderSlots(slots) {
  // 空的也要显示：区分「核心没配提示词」与「头坏了」。
  const fmt = (b) => (b && b.length ? b.map(textOf).join("") : "（空）");
  const put = (el, v) => {
    el.textContent = v;
    el.classList.toggle("empty", v === "（空）");
  };
  put($("slot-system"), slots ? fmt(slots.system) : "（空）");
  put($("slot-premier"), slots ? fmt(slots.premier) : "（空）");
  // 浮动槽正文由核心给；核心尚无装配器 ⇒ 恒空，如实显示「尚未实现」。
  $("floating-body").textContent = slots && slots.floating && slots.floating.length
    ? slots.floating.map(textOf).join("")
    : "（核心侧装配器尚未实现）";
}

function setStatus(status, turn) {
  const el = $("status");
  el.textContent = `${status} · 第 ${turn} 步`;
  el.className = "chip " + (status === "Running" ? "running" : "live");
}

// ---- 右栏三块：提供商 / 会话 / 项目 ----

function kv(dl, pairs) {
  dl.innerHTML = "";
  for (const [k, v, dim] of pairs) {
    const dt = document.createElement("dt");
    dt.textContent = k;
    const dd = document.createElement("dd");
    dd.textContent = v === null || v === undefined || v === "" ? "（未设）" : String(v);
    if (dim || v === null || v === undefined || v === "") dd.classList.add("dim");
    dl.append(dt, dd);
  }
}

async function loadProviders() {
  providers = await (await fetch(core + "/providers")).json();
  renderProviders();
}

function renderProviders() {
  const box = $("prov-list");
  box.innerHTML = "";
  if (providers.length === 0) {
    const n = document.createElement("div");
    n.className = "note";
    n.textContent = "还没有提供商。在核心终端里执行 `provider set <id> --key <key>`。";
    box.appendChild(n);
    return;
  }
  for (const p of providers) {
    const wrap = document.createElement("div");
    wrap.style.marginBottom = "9px";

    const head = document.createElement("div");
    head.style.cssText = "display:flex;align-items:center;gap:8px;margin-bottom:2px";
    const name = document.createElement("span");
    name.className = "sid";
    name.style.cssText = "font-size:12px;color:var(--ink);flex:1";
    name.textContent = p.id;

    // 「它通不通」是这一块最要紧的问题——核心上一批刚加的 `provider.test` 正是为它。
    // 不填任何参数：用的是 provider 自己配置里的 key 与端点（key 能改不能看，浏览器自然也不持有）。
    const test = document.createElement("button");
    test.textContent = "自检";
    test.style.cssText = "padding:3px 8px;font-size:11px";
    test.onclick = () => {
      const cid = crypto.randomUUID().slice(0, 8);
      testCmd.set(cid, p.id);
      provNotes.set(p.id, { text: "正在测…", ok: null });
      renderProviders();
      command(cid, { kind: "provider.test", id: p.id });
    };
    head.append(name, test);

    const dl = document.createElement("dl");
    dl.className = "kv";
    // ⚠️ apiKey 永远不显示 —— GET /providers 只给 hasApiKey（key 能改不能看）。
    //    端点 / 认证方式是配置的一部分，要看得见才能核对（端点是「为什么连不上」的头号嫌疑）。
    kv(dl, [
      ["协议", p.protocol],
      ["模型", p.defaultModel],
      ["端点", p.endpoint],
      ["认证", p.authScheme],
      ["密钥", p.hasApiKey ? "已配置" : "未配置", !p.hasApiKey],
    ]);

    wrap.append(head, dl);

    // 自检结果**就地**落在该 provider 下面：核心回的是这句就够回答「通不通 + 为什么」。
    const note = provNotes.get(p.id);
    if (note) {
      const n = document.createElement("div");
      n.className = "note";
      n.style.cssText = "margin-top:4px;line-height:1.6";
      if (note.ok === true) n.style.color = "var(--accent-ink)";
      if (note.ok === false) n.style.color = "var(--danger)";
      n.textContent = note.text;   // 自带 ✓/✗ 前缀，别再补一个
      wrap.appendChild(n);
    }

    box.appendChild(wrap);
  }
}

function renderSessionPanel(state) {
  const spec = state.spec || {};
  const st = state.state || {};
  const p = providers.find((x) => x.id === spec.model?.providerId);
  const dl = document.createElement("dl");
  dl.className = "kv";
  kv(dl, [
    ["会话", spec.sessionId],
    ["提供商", spec.model?.providerId],
    ["模型", spec.model?.modelId],
    ["协议", spec.model?.protocol ?? p?.protocol],
    ["思考强度", spec.model?.effort],
    ["创建", shortTime(spec.createdAt)],
  ]);
  $("sess-detail").innerHTML = "";
  $("sess-detail").appendChild(dl);
}

function renderProjectPanel(state) {
  const spec = state.spec || {};
  const dl = document.createElement("dl");
  dl.className = "kv";
  kv(dl, [
    ["工作目录", spec.workingDirectory],
    ["项目键", spec.projectKey],
  ]);
  $("proj-detail").innerHTML = "";
  $("proj-detail").appendChild(dl);
}

// ---- 底栏：核心自述（GET /info + /health） ----
// 「我配了 provider 怎么看不到」这类问题九成是**数据根不对** —— 这是它唯一的答案来源。

async function loadCoreFacts() {
  try {
    const i = await (await fetch(core + "/info")).json();
    $("bar-core").innerHTML = "";
    const b1 = document.createElement("b"); b1.textContent = "控制面";
    const s1 = document.createElement("span"); s1.textContent = ` ${i.endpoints?.http ?? core}`;
    $("bar-core").append(b1, s1);

    $("bar-dataroot").innerHTML = "";
    const b2 = document.createElement("b"); b2.textContent = "数据根";
    const s2 = document.createElement("span"); s2.textContent = ` ${i.dataRoot ?? "?"}`;
    $("bar-dataroot").append(b2, s2);
    $("bar-dataroot").title = i.dataRoot ?? "";

    $("bar-cwd").innerHTML = "";
    const b3 = document.createElement("b"); b3.textContent = "工作目录";
    const s3 = document.createElement("span"); s3.textContent = ` ${dirName(i.workingDirectory)}`;
    $("bar-cwd").append(b3, s3);
    $("bar-cwd").title = i.workingDirectory ?? "";

    $("bar-version").innerHTML = "";
    const b4 = document.createElement("b"); b4.textContent = "版本";
    const s4 = document.createElement("span"); s4.textContent = ` ${i.coreVersion ?? "?"}`;
    $("bar-version").append(b4, s4);
  } catch {
    $("bar-core").innerHTML = '<b>控制面</b> <span class="bad">读不到 /info</span>';
  }
}

// ---- 五轴（义务 4 的落点） ----
// 轴表、标签、inContext 都从核心拉（单一真相来源是核心的 Axes.Table，头不许手抄）：
// 核心交 { name, label, values, inContext }，头只画控件。

let AXES = [];
async function loadAxes() {
  // 一次拉取拿到三样：轴表 + 告警线的两端（必须真的赋值，否则又成硬编码副本）。
  const cfg = await (await fetch(core + "/axes")).json();
  AXES = cfg.axes;
  ALERT_SAFETY = cfg.alertSafety;
  ALERT_FROM = cfg.alertFrom;
}

// 告警线的两端归核心。下面的初始值是占位，loadAxes() 会覆盖；
// 但若 loadAxes() 失败或没跑，占位值就真的在用 ⇒ 取空串（永不触发）：宁可漏报。
let ALERT_SAFETY = "";
let ALERT_FROM   = "";

// ⚠️ 轴名**不写字面量**，一律回去查核心给的表。
const axisNamed = (n) => AXES.find((a) => a.name === n);

function renderAxes(values) {
  axes = values || {};
  $("ax-safety").innerHTML = "";
  $("axes").innerHTML = "";

  for (const a of AXES) {
    const row = document.createElement("div");
    // inContext 由核心给（effort 不进上下文），头不手抄。
    row.className = "axis" + (a.inContext ? "" : " effort");

    const sel = document.createElement("select");
    sel.id = "ax-" + a.name;
    for (const v of a.values) {
      const o = document.createElement("option");
      o.value = v; o.textContent = v;
      if (axes[a.name] === v) o.selected = true;
      sel.appendChild(o);
    }
    sel.onchange = () => {
      if (!current) return;
      command(crypto.randomUUID().slice(0, 8),
        { kind: "session.setAxis", sessionId: current, axis: a.name, value: sel.value });
    };

    const lab = document.createElement("label");
    lab.textContent = a.label;

    // 安全轴在右栏最上面单独一份，这里跳过它免得出现两个控件；标签也要填（从核心来）。
    // #ax-safety 在 HTML 里就是那个 <select> 本身（不是容器）⇒ 必须 replaceWith，
    // 不能 appendChild（否则得到重复 id，且用户看见的是外层那个空下拉）。
    // ⚠️ 判据是「这条轴的取值里含着告警线那一端」——**不是**比 "safety" 字面量：
    //    轴名归核心，头这里只能按核心给的数据认。
    if (a.values.includes(ALERT_SAFETY)) {
      $("ax-safety-label").textContent = a.label;
      $("ax-safety").replaceWith(sel);
      continue;
    }

    row.appendChild(lab); row.appendChild(sel);
    $("axes").appendChild(row);
  }

  renderAlert();
}

// 跨轴告警：安全=生产 + 权限≥授权 → 告警（纯提示、不联动）。
// 判据用档位序（下标 ≥ ALERT_FROM 的下标）而不是枚举值——权限轴加档时枚举写法会漏。
function permAxis() {
  // ⚠️ 同样不写字面量：权限轴 = 取值里含着告警起始档的那条。
  return AXES.find((a) => a.values.includes(ALERT_FROM));
}

function renderAlert() {
  const perm = permAxis();
  const at = perm ? perm.values.indexOf(axes[perm.name]) : -1;

  // indexOf 找不到返回 -1，而 at >= -1 恒真 ⇒ 必须守 from >= 0；
  // ALERT_SAFETY === "" 表示核心还没把告警线交过来，那时不该按占位值判断。
  const from = perm ? perm.values.indexOf(ALERT_FROM) : -1;
  const safetyAxis = AXES.find((a) => a.values.includes(ALERT_SAFETY));
  const ready = ALERT_SAFETY !== "" && from >= 0 && !!safetyAxis;
  const risky = ready && axes[safetyAxis.name] === ALERT_SAFETY && at >= from;

  $("safety-row").className = risky ? "alert" : "";
  $("alert").textContent = risky
    ? "⚠️ 生产环境 + 权限 ≥ 授权：高危操作会被自动放行。确认这是你要的。"
    : "";
}

// ---- 声明 ----

async function loadClaims() {
  const all = await (await fetch(core + "/claims")).json();
  claims.clear();
  for (const c of all) {
    if (!claims.has(c.sessionId)) claims.set(c.sessionId, new Set());
    claims.get(c.sessionId).add(c.path);
  }
  renderClaims();
}

function renderClaims() {
  // 呈现的关键是「冲突」，所以把冲突顶到最上面。
  const paths = [...claims.entries()].flatMap(([sid, set]) => [...set].map((p) => ({ sid, p })));
  const conflicts = [];
  for (let i = 0; i < paths.length; i++) {
    for (let j = i + 1; j < paths.length; j++) {
      if (paths[i].sid === paths[j].sid) continue;
      const a = paths[i].p.toLowerCase(), b = paths[j].p.toLowerCase();
      if (a === b || a.startsWith(b + "\\") || b.startsWith(a + "\\"))
        conflicts.push(`${paths[i].sid} ✕ ${paths[j].sid}：${paths[i].p}`);
    }
  }

  $("claim-conflicts").innerHTML = "";
  for (const line of conflicts) {
    const el = document.createElement("div");
    el.className = "banner warn";
    el.textContent = "⚠️ 声明冲突 — " + line;
    $("claim-conflicts").appendChild(el);
  }

  const box = $("claims");
  box.innerHTML = claims.size === 0 ? '<div class="note">（没有声明）</div>' : "";
  for (const [sid, set] of claims) {
    for (const p of set) {
      const el = document.createElement("div");
      el.className = "claim";
      // p 来自磁盘上的 claims.json 路径，不是标记 ⇒ 用 textContent，不用 innerHTML。
      const who = document.createElement("span");
      who.className = "who";
      who.textContent = sid.slice(0, 8);
      const path = document.createElement("span");
      path.className = "p";
      path.textContent = p;
      el.append(who, path);
      box.appendChild(el);
    }
  }
}

// ---- WS ----

// 握手就绪闸：connect() 返回时 socket 还在 CONNECTING，wsReady 仍为 false，
// 而「连上之前就发」的调用点（bootstrap / openSession 的首屏发送）会在握手完成前跑完。
// 有界 2 秒：核心根本不在时照样放行，让调用点走各自的失败路径。
function connected() {
  if (wsReady) return Promise.resolve();
  return new Promise((resolve) => {
    const iv = setInterval(() => { if (wsReady) done(); }, 20);
    const to = setTimeout(done, 2000);
    function done() { clearInterval(iv); clearTimeout(to); resolve(); }
  });
}

function connect() {
  ws = new WebSocket(core.replace("http", "ws") + "/ws");

  // new WebSocket 是异步的：这行返回时 readyState 还是 CONNECTING，
  // 不能拿 readyState === OPEN 当「能发帧了吗」的判据 ⇒ 发送凭据交给显式开关 wsReady
  //（connect() 置 false，onopen 第一句置 true，onclose 置 false）。
  wsReady = false;

  ws.onopen = () => {
    // 第一句就置位：下面的 subscribe() 与 openSession() 都同步读它。
    wsReady = true;
    $("status").textContent = "已连接";
    $("status").className = "chip live";
    loadCoreFacts();

    // currentProject === null 是「核心当时没起来」的信号：在这里补跑 bootstrap()
    //（去重靠 bootPromise）——既处理首屏两条路的并发，也做核心后起时的自恢复。
    if (currentProject === null) {
      bootstrap().catch((e) => renderMeta("✗ 核心没连上：" + (e && e.message ? e.message : e), "error"));
      return;   // 还没有当前项目/会话，下面两行无从谈起（`current` 也必为 null）
    }

    // 重连必须重拉会话列表：since: null 只要从现在起的事件，断线窗口里（控制台）
    // 建的会话补不回来，也不会自愈。重复订阅安全（同 topic 换掉旧的）。
    loadSessions();
    loadProviders();
    subscribe(`project:${currentProject}`, null);
    if (current) openSession(current);   // 重连一律拉快照重建（唯一的降级路径）
  };

  ws.onclose = () => {
    wsReady = false;
    $("status").textContent = "已断开 · 3 秒后重连";
    $("status").className = "chip down";
    setTimeout(connect, 3000);
  };

  ws.onmessage = (e) => {
    const frame = JSON.parse(e.data);

    if (frame.kind === "ack") {
      // 判据是「我们刚发的那条 create 的回执」（frame.id === creating），
      // 不是「这个 sessionId 我不认识」：send / interrupt 的回执也带 sessionId，
      // 且服务端是先发 session.created 再回 ack，两条帧先后不定。
      if (frame.accepted && frame.id === creating) {
        creating = null;
        // 建完要打开它：先 loadSessions()（把新会话收进 sessions 并订上项目流）
        // 再 openSession()。必须带 catch——reject 时会话停在「建了没打开」，
        // 且留下未观测 rejection。
        loadSessions().then(() => openSession(frame.sessionId))
                      .catch((e) => renderMeta("✗ " + e.message, "error"));
      }
      // note 是「业务结果」（provider 自检这类）——有就显示，别吞掉。
      // ⚠️ provider.test 的**失败也走 accepted{note}**（连不上/401 是业务结果，不是命令写错），
      //    所以自检的成败只能从 note 的**内容**读，读不出 ok 标志。判据是核心那句固定开头。
      if (frame.accepted && frame.id && testCmd.has(frame.id)) {
        const pid = testCmd.get(frame.id);
        testCmd.delete(frame.id);
        const note = (frame.note || "").trim();
        // ⚠️ 成不成**不能自己判**（比如 `!note.includes("不通")`）——那样「✗ 配不出适配器」
        //    会被判成成功。核心的 note **自带 ✓/✗ 前缀**，照着读就行：这正是
        //    「同一事实别在两处各推一遍」那条（核心已经在 note 里说清楚了）。
        const ok = note.startsWith("✓") ? true : note.startsWith("✗") ? false : null;
        provNotes.set(pid, { text: note, ok });
        renderProviders();
      } else if (frame.accepted && frame.note) {
        renderMeta("ℹ " + frame.note);
      }
      if (!frame.accepted) renderMeta("✗ " + frame.reason, "error");
      return;
    }

    if (frame.kind === "resync_required") {
      // 降级路径只有一条：拉快照重建（不做「部分合并」——那是另一套状态机）。
      renderMeta("（事件跟不上，已重新拉取快照）");
      cursors.delete(frame.topic);
      if (current && frame.topic === `session:${current}`) openSession(current);
      // 两条都要：loadClaims() 不发 subscribe，重订项目流只靠 loadSessions() 末尾那句；
      // resync 后旧订阅的 channel 已被 TryComplete() ⇒ 少调则项目流永久静默、不自愈。
      if (frame.topic.startsWith("project:")) { loadSessions(); loadClaims(); }
      return;
    }

    if (frame.kind !== "event") return;

    // 每流独立游标：只丢弃重复，不排序——排序是核心的保证。
    if (frame.seq <= (cursors.get(frame.topic) ?? 0)) return;
    cursors.set(frame.topic, frame.seq);

    if (frame.topic.startsWith("project:")) { handleProjectEvent(frame.payload); return; }
    handleEvent(frame.payload);
  };
}

function handleProjectEvent(p) {
  switch (p.kind) {
    case "session.created":
      loadSessions(); break;
    // 声明变化必须刷新右栏，否则「谁在动哪些文件」只在开页面那一刻是对的。
    case "claim.acquired":
    case "claim.released": loadClaims(); break;
  }
}

function handleEvent(p) {
  switch (p.kind) {
    case "message.appended":
      if (p.sessionId === current) renderTurn(p.from, p.blocks);
      break;

    case "session.step.begin":
      // 「在跑」的信号源是它：session.state 是磁盘的镜像，而磁盘从不存 Running。
      if (p.sessionId === current) setStatus("Running", p.turn - 1);
      break;

    case "session.step.end":
      if (p.sessionId === current) {
        setStatus("Idle", p.turn);
        if (p.refused) renderMeta("⚠️ 模型拒绝了这次请求");
        else if (["max_tokens", "length", "incomplete"].includes(p.stopReason))
          renderMeta(`（输出被截断了：${p.stopReason}）`);
      }
      break;

    case "session.state":
      if (p.sessionId === current) {
        setStatus(p.status, p.turn);
        const v = info.get(p.sessionId);
        if (v) { v.turn = p.turn; v.status = p.status; renderSessions(); }
      }
      break;

    case "axis.changed":
      // 只在真的迁移时才来（核心侧保证），这里直接重渲染即可。
      if (p.sessionId === current) { axes[p.axis] = p.value; renderAxes(axes); }
      break;

    case "error":
      // 业务失败走这里（不是 rejected：命令本身没写错）。
      if (p.sessionId === current) renderMeta("✗ " + p.message, "error");
      break;
  }
}

// ---- 命令 ----

function command(id, payload) {
  // 同 subscribe：判据是 wsReady。
  if (!wsReady) { renderMeta("✗ 没连上核心", "error"); return; }
  ws.send(JSON.stringify({ kind: "command", id, payload }));
}

function send() {
  const text = $("text").value.trim();
  if (!text || !current) return;
  command(crypto.randomUUID().slice(0, 8),
    { kind: "session.send", sessionId: current, blocks: [{ kind: "text", text }] });
  $("text").value = "";
}

// ---- 接线 ----

$("send").onclick = send;
$("text").onkeydown = (e) => { if (e.key === "Enter") send(); };
$("new").onclick = () => {
  const cmdId = crypto.randomUUID().slice(0, 8);
  creating = cmdId;
  command(cmdId, { kind: "session.create" });
};
$("interrupt").onclick = () => {
  if (current) command(crypto.randomUUID().slice(0, 8), { kind: "session.interrupt", sessionId: current });
};

// GET /providers 是引导要用的：端点不存在时 bootstrap() 会在 fetch().json() 上抛，
// 而那是最先跑的异步动作 ⇒ 整个页面停在空白。

(async () => {
  try { core = (await (await fetch("/config.json")).json()).core; } catch { }   // 取不到就用默认值
  connect();
  // 失败必须可见：不接的话是一次未观测拒绝（页面停在空白）；接住并打一行到时间线，
  // 核心起来后由 onopen 的 currentProject === null 分支自动重试。
  try {
    await bootstrap();
  } catch (e) {
    renderMeta("✗ 核心没连上（" + (e && e.message ? e.message : e) + "）——连上后会自动重试", "error");
  }
})();
