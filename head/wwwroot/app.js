// ⚠️ **零构建**（用户裁定）：原生 JS，没有打包、没有依赖、没有类型。
// 页面**直连核心**（`03` §1 的 WS + HTTP）——头进程不代理。
//
// ⚠️ **头的第一原则是反话**（`05` §1）：「核心越克制，头越要喧哗。」
// 核心为缓存优化，头为**人的注意力**优化。所以这里默认「多显示」而不是「省着显示」。
//
// ⚠️ 按 `05` §4 的四条义务逐条对：
//   ① 两个槽位原样显示 —— **做了**（并排，且空的也显示）
//   ② 声明可见       —— **做了**（保守：只做「声明主动」那一格，不做写入时自动检查）
//   ③ 审批走独立流   —— **阻塞**：核心侧闸门不存在，页面**明说**而不是显示空壳
//   ④ 安全轴显眼     —— **做了**（右栏最上 + 跨轴告警）
// ⚠️ 头仍是 `05` §1.1 的**可替换件**——义务必须履行，其余全是实现自由。

const $ = (id) => document.getElementById(id);

let core = "http://127.0.0.1:7788";
let ws = null;
let wsReady = false;                // ⚠️ 见 connect()：**只有它才是「可以发帧了吗」的判据**
let current = null;                 // 当前会话 id（**视图**，不是真相）
let currentProject = null;
const sessions = new Map();         // sessionId -> { projectKey }  ← §6.1：派生索引
const cursors = new Map();          // topic -> lastSeq           ← §7：每流一个游标
const claims = new Map();           // sessionId -> Set<path>     ← 声明（义务 2）
let axes = {};                      // 当前会话的轴值

// ---- 引导（`05` §2.2：**这是头的行为**，核心不做策略） ----
//
// ⚠️ 核心启动后**没有任何会话**（`01` §8.1），也没有任何提供商。
// 「配置提供商 → 建初始会话」是**头的策略**——它正是头比控制台强的地方
// （控制台没有视图，做不了这个判断）。
//
// ⚠️ **幂等是硬要求**：这段每次开页面都跑，所以「项目下有会话就打开它」——
// 少了它，每开一次浏览器就多一个会话（`01` §8.1）。
async function bootstrap() {
  // ⚠️ **轴表先拉**（G8：头不自带副本）。少了这一步，`AXES` 是空数组
  // ⇒ 右栏一个控件都画不出来，而用户看到的「没有轴」与「头坏了」分不清。
  await loadAxes();
  await loadSessions();

  // ① 有没有提供商？——没有就不能做别的（§2.2：引导页此时**挡住**其余操作）
  const providers = await (await fetch(core + "/providers")).json();
  if (!providers || providers.length === 0) { showOnboarding(); return; }
  hideOnboarding();

  // ② 当前项目下有会话？
  if (sessions.size === 0) {
    // ⇒ **由头调** session.create（核心只认命令，谁来调是头的事）
    // ⚠️ **先等连接就绪**：`command()` 在 `!wsReady` 时按设计**拒绝发送**，而 `connect()`
    // 返回时握手还没完成 ⇒ 不等的话这条首屏创建是**掷硬币**（见 `connected()`）。
    await connected();
    command("boot-create", { kind: "session.create" });
    return;
  }

  // ③ 有 ⇒ 打开最近的（**不新建**——见上面的幂等）
  openSession([...sessions.keys()][sessions.size - 1]);
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
  // ⚠️ §6.1：**全局会话列表是派生的**——对每个 pk 取 sessions 的并集。头自己拼。
  const projects = await (await fetch(core + "/projects")).json();
  sessions.clear();
  for (const pk of projects) {
    if (currentProject === null) currentProject = pk;
    const ids = await (await fetch(`${core}/projects/${pk}/sessions`)).json();
    for (const id of ids) sessions.set(id, { projectKey: pk });
  }
  renderSessions();

  // ⚠️ **项目流订阅在这里补一次**（第二个入口，见 `connect()` 的时序分析）。
  // 首屏那条路上 `connect()` 的 `onopen` 跑在 `loadSessions()` 之前 ⇒ 它那行被 `null` 跳过；
  // 而本函数正处在「`currentProject` 刚刚被填好」的那一刻 ⇒ 由它来订。
  // ⚠️ **重复订阅是安全的**：核心侧 `ControlServer.PumpAsync` 的 `Subscribe` 对同 topic
  // **换掉旧的**（它要的正是这个语义，见那里「重复订阅必须换掉那条旧的」的注释）；
  // 而 `since: null` = 「只要从现在开始的」⇒ 在任一顺序下都恰好订上一次。
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

// ---- 打开会话：先快照、再带高水位订阅（`03` §7 的原子性） ----

async function openSession(id) {
  // ⚠️ **先等连接就绪**：本函数末尾的 `subscribe()` 在 `!wsReady` 时会**静默返回**，
  // 而它可能是首屏/重连的第一条发送（`bootstrap`→`openSession`、`onopen`→`openSession`）
  // ⇒ 不等的话，会话流可能根本没订上，而页面以为订过了（见 `connected()`）。
  await connected();
  current = id;
  renderSessions();
  $("title").textContent = id;
  $("timeline").innerHTML = "";

  const state = await (await fetch(`${core}/sessions/${id}/state`)).json();
  const history = await (await fetch(`${core}/sessions/${id}/history`)).json();

  renderSlots(state.slots);
  renderAxes(state.axes);
  setStatus(state.state.status, state.state.turn);
  for (const turn of history.rendered) renderTurn(turn.from, turn.blocks);

  await loadClaims();

  // ⚠️ **用快照带回来的高水位当起点**——「拉快照 → 订阅」之间那段窗口不漏不重的唯一办法。
  subscribe(`session:${id}`, history.highWater);
}

function subscribe(topic, since) {
  // ⚠️ 判据是 `wsReady`，**不是 `ws.readyState`**——见 `connect()` 那段。
  if (!wsReady) return;
  ws.send(JSON.stringify({ kind: "subscribe", topic, since }));
}

// ---- 渲染：**核心渲染，头只显示**（`05` §3） ----

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
  // ⚠️ **空的也要显示**（§7.1）：`（空）` 与「有这个槽位但没内容」是两回事，
  // 而用户要能分清「核心没配提示词」与「头坏了」。
  const fmt = (b) => (b && b.length ? b.map(textOf).join("") : "（空）");
  $("slot-system").textContent = slots ? fmt(slots.system) : "（空）";
  $("slot-premier").textContent = slots ? fmt(slots.premier) : "（空）";
  // ⚠️ 浮动槽的正文由**核心**给（`05` §3：核心渲染）。核心还没有装配器 ⇒ 它恒空，
  // 而页面**如实显示「尚未实现」**——不假装它是空的提示词。
  $("floating-body").textContent = slots && slots.floating && slots.floating.length
    ? slots.floating.map(textOf).join("")
    : "（核心侧装配器尚未实现）";
}

function setStatus(status, turn) {
  $("status").textContent = `${status} · 第 ${turn} 步`;
}

// ---- 五轴（义务 4 的落点） ----
//
// ⚠️ **轴表从核心拉，头不自带副本**（G8 / `Axes.cs:8`：「任何别处（头的下拉…）
// 都从这张表读，**不许手抄**」）。加一条轴、改一个档，只改核心 `Axes.Table` 一处。
//
// ⚠️ 标签（`主动性` / `安全` …）与 `inContext` 也**归核心**——`05` §3「渲染归核心，头只显示」，
// 而下拉的标签就是渲染。核心交 `{ name, label, values, inContext }`，头只画控件。
// ⚠️ 由此 `GET /axes` 端点在 **T4**，而 `Axes` 要补上 `01` §3.5 的中文名（那是唯一真相来源，不是头）。
let AXES = [];
async function loadAxes() {
  // ⚠️ **一次拉取拿到三样**：轴表本身 + 告警线的两端。
  // ⚠️ **告警两端必须被真的覆盖**——只声明 `let ALERT_SAFETY = "prod"` 而不赋值，
  // 那两个字面量就还是硬编码的副本（「静默失效」的入口），白写了 `GET /axes` 的那两个字段。
  const cfg = await (await fetch(core + "/axes")).json();
  AXES = cfg.axes;
  ALERT_SAFETY = cfg.alertSafety;
  ALERT_FROM = cfg.alertFrom;
}

// ⚠️ **告警线的两端也归核心**：`安全=生产` 的档名与「权限≥授权」的起始档，
// 头里各写一个字面量就是**两份副本**——改一个档名就静默失效（告警不再响，而用户以为它在守）。
//
// ⚠️ 下面的初始值是**占位**，`loadAxes()` 会用核心交的值覆盖它。
// ⚠️ 但「有初始值」这件事本身有代价：**如果 `loadAxes()` 失败或没跑**，
// 这两个占位值就成了真的在用——那时告警按一份**没人维护的副本**判断。
// ⇒ 占位值取**永不触发告警**的那个（空串），宁可漏报也不误报：
// 漏报的表现是「该响没响」，而误报会让用户学会无视这个告警。
// ⚠️ 这也解释了为什么 `bootstrap()` 的第一句必须是 `await loadAxes()`。
let ALERT_SAFETY = "";
let ALERT_FROM   = "";

function renderAxes(values) {
  axes = values || {};
  $("ax-safety").innerHTML = "";
  $("axes").innerHTML = "";

  for (const a of AXES) {
    const row = document.createElement("div");
    // ⚠️ `inContext` 由**核心**给（`01` §3.5：`effort` 不进上下文）——
    // 头不再靠手抄的 `effort: true` 标记，那又是一份副本。
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

    // ⚠️ **安全轴放在右栏最上面单独一份**（义务 4），这里跳过它免得出现两个控件。
    // ⚠️ 但**标签也要填**——它是从核心来的（`05` §3：渲染归核心），不能因为换了位置就丢掉。
    // ⚠️ **是 `replaceWith`，不是 `appendChild`**：`#ax-safety` 在 HTML 里**就是那个 `<select>`
    // 本身**（不是容器）⇒ append 会得到 `<select id="ax-safety"><select id="ax-safety">…`：
    // 重复 id，且外层那个空下拉才是用户看见的（本任务 Step 3 实测：`#ax-safety.options` 长度为 0）。
    // ⇒ 用刚建好的 `sel` **顶替**那个占位 select（同一个 id，选项与 onchange 都是新的）。
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

/// ⚠️ **跨轴告警**（`01` §3.5 定的那条）：`安全=生产` + `权限≥授权` → 告警。
/// ⚠️ 它是**纯提示、不联动**——不设「生产 ⇒ 权限封顶」，那是家长式干预且会掩盖
/// 「安全是建议」这个诚实的定位。
/// ⚠️ **跨轴告警**（`01` §3.5 定的那条）：`安全=生产` + `权限≥授权` → 告警。
/// ⚠️ 它是**纯提示、不联动**——不设「生产 ⇒ 权限封顶」，那是家长式干预且会掩盖
/// 「安全是建议」这个诚实的定位。
///
/// ⚠️ **两头都读核心交的值，不写字面量**：`ALERT_SAFETY` 是安全轴里代表生产的**档名**，
/// `ALERT_FROM` 是权限轴里「授权及以上」的**起始档**。写字面量的话，
/// 轴里改一个档名就会让告警**静默失效**——用户以为它在守，而它再也不响。
///
/// ⚠️ **判据是「档位序」而不是「是不是这两个值之一」**：权限是**单调阶梯**（`01` §3.5，
/// 0 禁用 … 5 信任），所以 `≥授权` 的正确写法是「在表里的下标 ≥ `ALERT_FROM` 的下标」。
/// 今天 `AT_OR_ABOVE` 恰好等于 {authorized, trusted} 两个，但那是**巧合**——
/// 权限轴加第 6 档时，枚举写法会漏掉它，而序数写法自动覆盖。
function alertFromIndex() {
  const perm = AXES.find((a) => a.name === "permission");
  return perm ? perm.values.indexOf(ALERT_FROM) : -1;
}

function renderAlert() {
  const perm = AXES.find((a) => a.name === "permission");
  const at = perm ? perm.values.indexOf(axes.permission) : -1;

  // ⚠️ **每一端都必须先「解析出来」才算数。**
  // `indexOf` 找不到时返回 `-1`，而 `at >= -1` **恒真** ⇒ 少了 `from >= 0` 这道守卫，
  // 只要安全档恰好相符就会**误报**（而误报会让用户学会无视这个告警）。
  // 同理 `ALERT_SAFETY === ""` 表示「核心还没把告警线交过来」（见 `loadAxes` 的注释），
  // 那时**不该**按一个占位值判断。
  const from = alertFromIndex();
  const ready = ALERT_SAFETY !== "" && from >= 0;
  const risky = ready && axes.safety === ALERT_SAFETY && at >= from;

  $("safety-row").className = risky ? "alert" : "";
  $("alert").textContent = risky
    ? "⚠️ 生产环境 + 权限 ≥ 授权：高危操作会被**自动放行**。确认这是你要的。"
    : "";
}

// ---- 声明（义务 2） ----

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
  // ⚠️ §7.2：**呈现的关键不是列表，是「冲突」**。
  // 列表画在下面，冲突**顶到最上面**——因为树里（或者列表里）的 ⚠ 会被忽略。
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
      el.innerHTML = `<span class="who">${sid}</span> ${p}`;
      box.appendChild(el);
    }
  }
}

// ---- WS ----

// ⚠️ **握手就绪闸**：`connect()` 只是**发起**握手，它返回时 socket 还在 `CONNECTING`，
// 而 `wsReady` 仍是 false。
// ⚠️ 于是「连上之前就发」和「没连上硬发」成了两回事：`subscribe()` / `command()` 的
// `if (!wsReady)` 挡住了后者（对——那是为了不静默丢帧），却**挡不住前者**：
// `bootstrap()` / `openSession()` 的 `fetch` 完全可能在握手完成**之前**跑完 ⇒ 那两处首屏发送
// （② 建会话的命令、`openSession` 末尾的会话流订阅）撞上 `!wsReady` 被**静默丢掉**，
// 而页面**不会自愈**（`openSession` 以为订过了，`bootstrap` 以为建过了）。
// ⚠️ 这与 `connect()` 注释里追求的「**任一顺序都订上**」是同一件事：光有 `wsReady` 只把
// 判据从「读 socket 瞬时状态」收敛到「显式开关」，还要**等它翻转**才算完。
// ⚠️ 本任务 Step 3 实测到过这一条：首次开页面时 `bootstrap` 的 `session.create` 落在
// `!wsReady` 上，时间线只剩「✗ 没连上核心」，左边一个会话都没有。
// ⚠️ **有界**（2 秒）：核心根本不在时照样放行，让调用点走各自的失败路径（报错/空态），
// 不把页面吊死在这个 await 上。
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

  // ⚠️ **`new WebSocket` 是异步的**：这一行返回时 `readyState` 还是 `CONNECTING(0)`。
  // ⇒ 拿 `readyState === OPEN` 当「能发帧了吗」的判据**是错的**——
  //   它在建连**之前**判 false（错得明显、反而看得见），但在建连**途中**也判 false，
  //   于是「按顺序写在 `onopen` 之后」的那行其实**根本没发出去**，而且**静默失败**。
  // ⚠️ 本文件有三处发送点都在 `onopen` 之后：项目流订阅、`openSession`（内含 `subscribe`）、
  //   `bootstrap`→`openSession`。**首屏那一处必然静默**（见下面 `onopen` 的时序分析）。
  // ⇒ **发送凭据交给显式开关**：`connect()` 置 false，`onopen` 第一句置 true，`onclose` 置 false。
  //   它有唯一的所有者与唯一的判据，不需要读 socket 的瞬时状态。
  wsReady = false;

  ws.onopen = () => {
    // ⚠️ **第一句就置位**——下面的 `subscribe()` 与 `openSession()` 都同步读它。
    wsReady = true;
    $("status").textContent = "已连接";
    // ⚠️ **项目流**：会话列表是派生的（§6.1），靠 `session.created` 更新；
    // 声明也走这条流（`03` §5）。不订它的话，在**控制台**建会话或占区域，
    // 这个页面看不见，且不会自愈。
    //
    // ⚠️ **两条路径把它补上，合起来是完备的**：
    //   ① **首屏**：`connect()` 在 `CONNECTING(0)` 时就跑完了，所以这里的 `currentProject`
    //      还是 `null`（`loadSessions()` 由 `bootstrap()` 里的 `await` 才跑）⇒ 这一行**跳过**；
    //      补它的是 `loadSessions()` 末尾自己那句 `subscribe(...)`（见那里）。
    //   ① **重连**：`currentProject` 已非空 ⇒ 这一行**订上**；且 `openSession(current)`
    //      会在同一段同步代码里重新 `subscribe` 会话流。
    // ⇒ **不要靠「WebSocket 比 fetch 慢，所以多半能赢」**——那是掷硬币（首屏实测就是跳过）。
    //   要的是「任一顺序都订上」：两个入口各自兜住一种顺序。
    if (currentProject) subscribe(`project:${currentProject}`, null);
    if (current) openSession(current);   // 重连一律拉快照重建（§6.2 唯一的降级路径）
  };

  ws.onclose = () => {
    wsReady = false;
    $("status").textContent = "已断开（3 秒后重连）";
    setTimeout(connect, 3000);
  };

  ws.onmessage = (e) => {
    const frame = JSON.parse(e.data);

    if (frame.kind === "ack") {
      if (frame.accepted && frame.sessionId && !sessions.has(frame.sessionId)) loadSessions();
      if (!frame.accepted) renderMeta("✗ " + frame.reason, "error");
      return;
    }

    if (frame.kind === "resync_required") {
      // ⚠️ §6.2：降级路径**只有一条**——拉快照重建。
      // **不发明「部分合并」**：那是另一套状态机，且必然与快照对不齐。
      renderMeta("（事件跟不上，已重新拉取快照）");
      cursors.delete(frame.topic);
      if (current && frame.topic === `session:${current}`) openSession(current);
      if (frame.topic.startsWith("project:")) loadClaims();
      return;
    }

    if (frame.kind !== "event") return;

    // ⚠️ **每流独立的游标**（§7）。只丢弃重复，**不排序**——排序是核心的保证。
    if (frame.seq <= (cursors.get(frame.topic) ?? 0)) return;
    cursors.set(frame.topic, frame.seq);

    if (frame.topic.startsWith("project:")) { handleProjectEvent(frame.payload); return; }
    handleEvent(frame.payload);
  };
}

function handleProjectEvent(p) {
  switch (p.kind) {
    case "session.created": loadSessions(); break;
    // ⚠️ 义务 2 的实时性靠这两条——声明变化**必须**刷新右栏，
    // 否则「谁在动哪些文件」只在开页面那一刻是对的。
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
      // ⚠️ 「在跑」的信号源是它，不是 `session.state`——后者是磁盘的镜像，而磁盘**从不存 Running**。
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
      // ⚠️ 只在**真的迁移**时来（核心侧已保证），所以这里直接重渲染即可。
      if (p.sessionId === current) { axes[p.axis] = p.value; renderAxes(axes); }
      break;

    case "error":
      // ⚠️ **业务失败走这里**（`03` §3 的分界线）——它不是 `rejected`（命令没写错）。
      if (p.sessionId === current) renderMeta("✗ " + p.message, "error");
      break;
  }
}

// ---- 命令（`03` §3） ----

function command(id, payload) {
  // ⚠️ 同 `subscribe`：判据是 `wsReady`。
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
$("new").onclick = () => command(crypto.randomUUID().slice(0, 8), { kind: "session.create" });
$("interrupt").onclick = () => {
  if (current) command(crypto.randomUUID().slice(0, 8), { kind: "session.interrupt", sessionId: current });
};

// ⚠️ **`GET /providers` 是头的引导要用的**（`05` §2.2）：没有提供商时**不能做别的**。
// 端点不存在的话 `bootstrap()` 会在 `fetch(...).json()` 上抛，
// 而那是**第一条**要跑的异步动作 ⇒ 整个页面停在空白，控制台里一条错。
// ⇒ 它在 `ControlServer.MapSnapshots` 里加。

(async () => {
  try { core = (await (await fetch("/config.json")).json()).core; } catch { /* 用默认值 */ }
  connect();
  await bootstrap();
})();
