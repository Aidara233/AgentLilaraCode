// 零构建：原生 JS，无打包、无依赖、无类型。页面直连核心（WS + HTTP），头不代理。
// 头是可替换件——履行义务即可，其余是实现自由。

const $ = (id) => document.getElementById(id);

let core = "http://127.0.0.1:7788";
let ws = null;
let wsReady = false;                // 唯一的「可以发帧了吗」判据（见 connect()）
let current = null;                 // 当前会话 id（视图，非真相）
let epoch = 0;                      // 每次 openSession 自增，用来区分同 id 的并发重入
let creating = null;                // 刚发出的 session.create 的命令 id
let currentProject = null;
const sessions = new Map();         // sessionId -> { projectKey }（派生索引）
const cursors = new Map();          // topic -> lastSeq，每流一个游标
const claims = new Map();           // sessionId -> Set<path>（声明）
let axes = {};                      // 当前会话的轴值

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

  // ① 没有提供商就不能做别的（引导页挡住其余操作）
  const providers = await (await fetch(core + "/providers")).json();
  if (!providers || providers.length === 0) { showOnboarding(); return; }
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
  $("onboarding").style.display = "block";
  $("text").disabled = true;
  $("send").disabled = true;
}

function hideOnboarding() {
  $("onboarding").style.display = "none";
  $("text").disabled = false;
  $("send").disabled = false;
}

async function loadSessions() {
  // 全局会话列表是派生的：对每个项目取 sessions 的并集。
  const projects = await (await fetch(core + "/projects")).json();
  sessions.clear();
  for (const pk of projects) {
    if (currentProject === null) currentProject = pk;
    const ids = await (await fetch(`${core}/projects/${pk}/sessions`)).json();
    for (const id of ids) sessions.set(id, { projectKey: pk });
  }
  renderSessions();

  // 在这里补订项目流。重复订阅是安全的：核心对同 topic 换掉旧的，
  // 且 since: null 只要从现在起的事件 ⇒ 任一顺序下恰剩一条有效订阅。
  if (currentProject) subscribe(`project:${currentProject}`, null);
}

function renderSessions() {
  const box = $("sessions");
  box.innerHTML = "";
  if (sessions.size === 0) { box.innerHTML = '<div class="muted">还没有会话</div>'; return; }
  for (const id of sessions.keys()) {
    const el = document.createElement("div");
    el.className = "session" + (id === current ? " active" : "");
    el.textContent = id;
    el.onclick = () => openSession(id);
    box.appendChild(el);
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
  renderSessions();
  $("title").textContent = id;
  $("timeline").innerHTML = "";

  const state = await (await fetch(`${core}/sessions/${id}/state`)).json();
  const history = await (await fetch(`${core}/sessions/${id}/history`)).json();

  // 快照回来时用户可能已开了别的会话——这些是全局节点，继续渲染会把上一次的
  // turn 追加进已经清空的那条时间线里。
  if (gen !== epoch) return;

  renderSlots(state.slots);
  renderAxes(state.axes);
  setStatus(state.state.status, state.state.turn);
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

function textOf(blocks) {
  return (blocks || []).filter((b) => b.kind === "text").map((b) => b.text).join("");
}

function renderTurn(from, blocks, cls) {
  const el = document.createElement("div");
  el.className = "turn " + (cls || from);
  el.textContent = (from === "user" ? "你  " : "⏺  ") + (textOf(blocks) || "（这一步没有正文输出）");
  $("timeline").appendChild(el);
  $("timeline").scrollTop = $("timeline").scrollHeight;
}

function renderMeta(text, cls) {
  const el = document.createElement("div");
  el.className = "turn meta " + (cls || "");
  el.textContent = text;
  $("timeline").appendChild(el);
  $("timeline").scrollTop = $("timeline").scrollHeight;
}

function renderSlots(slots) {
  // 空的也要显示：区分「核心没配提示词」与「头坏了」。
  const fmt = (b) => (b && b.length ? b.map(textOf).join("") : "（空）");
  $("slot-system").textContent = slots ? fmt(slots.system) : "（空）";
  $("slot-premier").textContent = slots ? fmt(slots.premier) : "（空）";
  // 浮动槽正文由核心给；核心尚无装配器 ⇒ 恒空，如实显示「尚未实现」。
  $("floating-body").textContent = slots && slots.floating && slots.floating.length
    ? slots.floating.map(textOf).join("")
    : "（核心侧装配器尚未实现）";
}

function setStatus(status, turn) {
  $("status").textContent = `${status} · 第 ${turn} 步`;
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
    if (a.name === "safety") {
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
function alertFromIndex() {
  const perm = AXES.find((a) => a.name === "permission");
  return perm ? perm.values.indexOf(ALERT_FROM) : -1;
}

function renderAlert() {
  const perm = AXES.find((a) => a.name === "permission");
  const at = perm ? perm.values.indexOf(axes.permission) : -1;

  // indexOf 找不到返回 -1，而 at >= -1 恒真 ⇒ 必须守 from >= 0；
  // ALERT_SAFETY === "" 表示核心还没把告警线交过来，那时不该按占位值判断。
  const from = alertFromIndex();
  const ready = ALERT_SAFETY !== "" && from >= 0;
  const risky = ready && axes.safety === ALERT_SAFETY && at >= from;

  $("safety-row").className = risky ? "alert" : "";
  $("alert").textContent = risky
    ? "⚠️ 生产环境 + 权限 ≥ 授权：高危操作会被**自动放行**。确认这是你要的。"
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
    el.className = "banner";
    el.textContent = "⚠️ 声明冲突 — " + line;
    $("claim-conflicts").appendChild(el);
  }

  const box = $("claims");
  box.innerHTML = claims.size === 0 ? '<div class="muted">（没有声明）</div>' : "";
  for (const [sid, set] of claims) {
    for (const p of set) {
      const el = document.createElement("div");
      el.className = "claim";
      // p 来自磁盘上的 claims.json 路径，不是标记 ⇒ 用 textContent，不用 innerHTML。
      const who = document.createElement("span");
      who.className = "who";
      who.textContent = sid;
      el.appendChild(who);
      el.appendChild(document.createTextNode(" " + p));
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

    // currentProject === null 是「核心当时没起来」的信号：在这里补跑 bootstrap()
    //（去重靠 bootPromise）——既处理首屏两条路的并发，也做核心后起时的自恢复。
    if (currentProject === null) {
      bootstrap().catch((e) => renderMeta("✗ 核心没连上：" + (e && e.message ? e.message : e), "error"));
      return;   // 还没有当前项目/会话，下面两行无从谈起（`current` 也必为 null）
    }

    // 重连必须重拉会话列表：since: null 只要从现在起的事件，断线窗口里（控制台）
    // 建的会话补不回来，也不会自愈。重复订阅安全（同 topic 换掉旧的）。
    loadSessions();
    subscribe(`project:${currentProject}`, null);
    if (current) openSession(current);   // 重连一律拉快照重建（唯一的降级路径）
  };

  ws.onclose = () => {
    wsReady = false;
    $("status").textContent = "已断开（3 秒后重连）";
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
    case "session.created": loadSessions(); break;
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
      if (p.sessionId === current) setStatus(p.status, p.turn);
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
