// ⚠️ **零构建**（用户裁定）：原生 JS，没有打包、没有依赖、没有类型。
// 页面**直连核心**（`03` §1 的 WS + HTTP）——头进程不代理。
//
// ⚠️ 这个头是**糊弄版**：它只做到「看得见 + 能说话」。
// 按 `05` §4 的四条义务逐条对：**只履行了第 1 条**（两个槽位原样显示）。
// 声明可见 / 审批 / 安全轴都**没有**——它们的核心侧机制根本不存在。
// ⇒ **别把它当成头的完成态**，它是可替换件里的第一版（`05` §1.1）。

const $ = (id) => document.getElementById(id);

let core = "http://127.0.0.1:7788";
let ws = null;
let current = null;
const sessions = new Map();   // sessionId -> { projectKey }
let currentProject = null;

// ⚠️ **游标必须每流一个**（`03` §7 / `05` §6.2：「每个订阅流各自一个游标」）。
// 第一版用**一个全局 `seq`**——切到另一个会话时，它的事件从小号开始，
// 会被 `frame.seq <= seq` **静默丢掉**，直到追上前一个会话的水位。
// 那就是「头少了一步而它不知道」，正是文档反复要防的那类。
const cursors = new Map();    // topic -> lastSeq

// ---- 会话（05 §6.1：全局会话列表是**派生**的——对每个 pk 取 sessions 的并集） ----

async function loadSessions() {
  const projects = await (await fetch(core + "/projects")).json();
  sessions.clear();

  for (const pk of projects) {
    if (currentProject === null) currentProject = pk;   // 最小实现只跟第一个项目
    const ids = await (await fetch(`${core}/projects/${pk}/sessions`)).json();
    for (const id of ids) sessions.set(id, { projectKey: pk });
  }
  renderSessions();
}

function renderSessions() {
  const box = $("sessions");
  box.innerHTML = "";
  if (sessions.size === 0) {
    box.innerHTML = '<div class="muted">还没有会话</div>';
    return;
  }
  for (const id of sessions.keys()) {
    const el = document.createElement("div");
    el.className = "session" + (id === current ? " active" : "");
    el.textContent = id;
    el.onclick = () => openSession(id);
    box.appendChild(el);
  }
}

// ---- 打开一个会话：**先快照、再带高水位订阅**（03 §7 的原子性） ----

async function openSession(id) {
  current = id;
  renderSessions();
  $("title").textContent = id;
  $("timeline").innerHTML = "";

  const state = await (await fetch(`${core}/sessions/${id}/state`)).json();
  const history = await (await fetch(`${core}/sessions/${id}/history`)).json();

  renderSlots(state.slots);
  setStatus(state.state.status, state.state.turn);

  for (const turn of history.rendered) renderTurn(turn.from, turn.blocks);

  // ⚠️ **用快照带回来的高水位当起点**——这是「拉快照 → 订阅」之间那段窗口不漏不重的**唯一**办法。
  // 不用它的话，那之间落的事件要么丢、要么重；而头是无状态的，刷新一次就重来一次。
  // （重连游标会让 `seq` 对不上，所以每次开新会话都**重新订阅**。）
  subscribe(id, history.highWater);
}

function subscribe(id, since) {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;
  ws.send(JSON.stringify({ kind: "subscribe", topic: `session:${id}`, since }));
}

// ---- 渲染 ----

function textOf(blocks) {
  return blocks.filter((b) => b.kind === "text").map((b) => b.text).join("");
}

function renderTurn(from, blocks, cls) {
  const el = document.createElement("div");
  el.className = "turn " + (cls || from);
  const text = textOf(blocks);
  el.textContent = (from === "user" ? "你  " : "⏺  ") + (text || "（这一步没有正文输出）");
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
  // ⚠️ **空的也显示**（`05` §7.1）——`（空）` 与「有这个槽位但没内容」是两回事，
  // 而用户要能分清「核心没配提示词」与「头坏了」。
  const fmt = (b) => (b && b.length ? b.map(textOf).join("") : "（空）");
  $("slot-system").textContent = slots ? fmt(slots.system) : "（空）";
  $("slot-premier").textContent = slots ? fmt(slots.premier) : "（空）";
}

function setStatus(status, turn) {
  $("status").textContent = `${status} · 第 ${turn} 步`;
}

// ---- WS ----

function connect() {
  ws = new WebSocket(core.replace("http", "ws") + "/ws");

  ws.onopen = () => {
    $("status").textContent = "已连接";

    // ⚠️ **项目流**：全局会话列表是**派生**的（`05` §6.1），靠 `session.created` 更新。
    // 不订它的话，在**控制台**敲 `session create`，这个页面看不见新会话，且不会自愈。
    ws.send(JSON.stringify({ kind: "subscribe", topic: `project:${currentProject}` }));

    if (current) {
      // 重连：核心那边的事件缓冲有界，我们**不假装记得**游标——
      // 直接重新拉快照重建（`03` §7 说这是**唯一**该有的降级路径）。
      openSession(current);
    }
  };

  ws.onclose = () => {
    $("status").textContent = "已断开（3 秒后重连）";
    setTimeout(connect, 3000);
  };

  ws.onmessage = (e) => {
    const frame = JSON.parse(e.data);

    if (frame.kind === "ack") {
      // ⚠️ 分配型命令的**唯一**回传身份的地方（`03` §2 的唯一例外）
      if (frame.accepted && frame.sessionId && !sessions.has(frame.sessionId)) {
        loadSessions();
      }
      if (!frame.accepted) renderMeta("✗ " + frame.reason, "error");
      return;
    }

    if (frame.kind === "resync_required") {
      // ⚠️ 跟不上 ⇒ 拉快照重建。**不发明「部分合并」**——那是另一套状态机，必然与快照对不齐。
      renderMeta("（事件跟不上，已重新拉取快照）");
      cursors.delete(frame.topic);
      if (current && frame.topic === `session:${current}`) openSession(current);
      return;
    }

    if (frame.kind !== "event") return;

    // ⚠️ **每流独立的游标**（`03` §7）。只做丢弃重复（重连时可能重放），
    // **不排序**——排序是核心的保证，头乱排只会掩盖核心的 bug。
    if (frame.seq <= (cursors.get(frame.topic) ?? 0)) return;
    cursors.set(frame.topic, frame.seq);

    // ⚠️ 项目流上的事件不带 sessionId 语义（它是跨会话那一层）
    if (frame.topic.startsWith("project:")) {
      if (frame.payload.kind === "session.created") loadSessions();
      return;
    }

    handleEvent(frame.payload);
  };
}

function handleEvent(p) {
  switch (p.kind) {
    case "message.appended":
      if (p.sessionId === current) renderTurn(p.from, p.blocks);
      break;

    case "session.step.begin":
      // ⚠️ **「在跑」的信号源是它，不是 `session.state`**——后者是磁盘的镜像，
      // 而磁盘**从不存 Running**。只信 state 的话，头连轮询都看不出在跑。
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

    case "error":
      // ⚠️ **业务失败走这里**（`03` §3 的分界线）——它不是 `rejected`（命令没写错）。
      if (p.sessionId === current) renderMeta("✗ " + p.message, "error");
      break;
  }
}

// ---- 命令（03 §3） ----

function command(id, payload) {
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    renderMeta("✗ 没连上核心", "error");
    return;
  }
  ws.send(JSON.stringify({ kind: "command", id, payload }));
}

function send() {
  const text = $("text").value.trim();
  if (!text || !current) return;

  command(crypto.randomUUID().slice(0, 8), {
    kind: "session.send",
    sessionId: current,
    blocks: [{ kind: "text", text }],
  });
  $("text").value = "";
}

// ---- 接线 ----

$("send").onclick = send;
$("text").onkeydown = (e) => { if (e.key === "Enter") send(); };
$("new").onclick = () => command(crypto.randomUUID().slice(0, 8), { kind: "session.create" });
$("interrupt").onclick = () => {
  if (current) command(crypto.randomUUID().slice(0, 8), { kind: "session.interrupt", sessionId: current });
};

(async () => {
  try {
    // ⚠️ 核心地址由**头进程**递进来（`/config.json`）——页面自己不该猜。
    core = (await (await fetch("/config.json")).json()).core;
  } catch { /* 用默认值 */ }

  await loadSessions();
  connect();
})();
