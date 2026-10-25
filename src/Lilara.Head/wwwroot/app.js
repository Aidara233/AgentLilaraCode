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

// ⚠️ 这是**兜底值**——正常路径是启动时拉 `/config.json`（见下方 `core = ...core`）。
// 它必须与 `head/Program.cs` 的默认核心地址一致（两份副本，改一个要跟着改另一个）。
let core = "http://127.0.0.1:17988";
let ws = null;
let wsReady = false;                // 唯一的「可以发帧了吗」判据（见 connect()）
let current = null;                 // 当前会话 id（视图，非真相）
let epoch = 0;                      // 每次 openSession 自增，用来区分同 id 的并发重入
const historyState = { id: null, cursor: null, more: false, loading: false };
let creating = null;                // 刚发出的 session.create 的命令 id
let currentProject = null;
const sessions = new Map();         // sessionId -> { projectKey }（派生索引，次序 = 显示次序）
const cursors = new Map();          // topic -> lastSeq，每流一个游标
const claims = new Map();           // sessionId -> Set<path>（声明）
// path -> true，那些**主人已经不在了**的声明（核心 `/claims` 的 `stale` 字段）。
// 单独一张表而不是塞进 `claims`：冲突判据只关心「谁占了什么」，而 stale 是**另一件事**
// （「这条还有没有人认」）——混进同一张表会让冲突逻辑被迫理解它不关心的维度。
const staleClaims = new Set();
let axes = {};                      // 当前会话的轴值
// 「重试 N/10 · X 秒后再试」这类**附着在状态上的事实**（由 retry.attempt 设、离开 Running 就清）。
// ⚠️ 单独一格而不是拼进 `status` 字符串：状态有它自己的身份（`setStatus` 里比较 `Running`），
// 拼进去会让「状态是什么」变成一个要被拆解的字符串。
let statusNote = null;

// 派生视图缓存：不发事件的东西靠它渲染（项目名 / 步数 / 状态）。
const projects = new Map();         // pk -> { dir?, count }
const info = new Map();             // sid -> { spec?, turn, status }
let providers = [];
let sessionContext = null;         // 当前会话最近一次核心报告的上下文 token
let usageSummary = null;           // 统计页缓存；事实仍由核心端点提供
let usageCalls = [];
let usageAsOf = null;
let usageSkip = 0;
const usagePageSize = 100;
// 归档的会话默认**折叠**在左栏里（⚠️ 过滤只发生在这里——核心的 `ListSessions`
// **照列**归档会话，理由见 `SessionSpec.ArchivedAt` 的注释：那是「一个项目下有哪些会话」
// 的唯一定义，而 `/claims` 也走它）。
let showArchived = false;
const provNotes = new Map();        // providerId -> { text, ok }（最近一次自检的结果）
const testCmd = new Map();          // 命令 id -> providerId（回执认得回来）
let editingProvider = null;         // 供应商视图里正在编辑的那个 id；null = 没有
let showNewCard = false;            // 「新建」那张卡要不要展开（默认收起——它不是数据，是入口）
let SCHEMA = { protocols: [], authSchemes: [] };   // GET /provider-schema（头的合法值来源，**不许手抄**）
const pendingAcks = new Map();      // 命令 id -> (frame) => void（`commandNow` 的回执关联）

function tokenText(value) {
  return value === null || value === undefined ? "未报告" : Number(value).toLocaleString();
}

// 比例（0..1）→ 百分比。⚠️ 与 tokenText 同一个判据：null = 上游没报，**不是 0%**。
// 分母未知时核心给的是 null（不是 0），这里照它显示「未报告」。
function rateText(rate) {
  if (rate === null || rate === undefined) return "未报告";
  // ⚠️ **截断，不四舍五入**：99.6% 舍入成 100% 就等于宣称「全命中」，而那正好是缓存诊断
  // 最想知道的一个区别（`08` §3.2 的验收就有一条：缓存明细不许把未命中算成命中）。
  const p = Math.max(0, Number(rate)) * 100;
  const digits = p > 0 && p < 10 ? 1 : 0;
  const f = 10 ** digits;
  return `${(Math.floor(p * f) / f).toFixed(digits)}%`;
}

function renderContextStatus(context) {
  sessionContext = context || null;
  const el = $("context-status");
  if (!el) return;
  el.classList.toggle("has-value", !!(context && context.latest));
  if (!context || !context.latest) {
    el.textContent = "上下文 输入 token 未报告";
    el.title = "核心尚未报告当前会话的输入 token";
    return;
  }
  const latest = context.latest;
  el.textContent = `上下文 ${tokenText(latest.inputTokens)}`;
  el.title = `最近一次输入 token：${tokenText(latest.inputTokens)} · ${latest.source === "reported" ? "模型报告" : "未报告"}${latest.startedAt ? ` · ${shortTime(latest.startedAt)}` : ""}`;
}

async function loadSessionContext(id) {
  try {
    const response = await fetch(`${core}/sessions/${id}/context`);
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const context = await response.json();
    if (id === current) renderContextStatus(context);
    return context;
  } catch (e) {
    if (id === current) renderContextStatus(null);
    return null;
  }
}

function usageBucketRow(bucket) {
  const tr = document.createElement("tr");
  const key = document.createElement("td");
  key.className = "key";
  key.textContent = bucket.key || "（未知）";
  const calls = document.createElement("td");
  calls.className = "num";
  calls.textContent = tokenText(bucket.calls);
  const input = document.createElement("td");
  input.className = "num";
  input.textContent = tokenText(bucket.inputTokens);
  const output = document.createElement("td");
  output.className = "num";
  output.textContent = tokenText(bucket.outputTokens);
  const hit = document.createElement("td");
  hit.className = "num";
  hit.textContent = rateText(bucket.cacheHitRate);
  // 悬停说明分母：命中率是在哪几笔上算的（未报告的调用不进分母，见 UsageSummary.CacheHitRate）。
  hit.title = `命中 ${tokenText(bucket.cacheHitTokens)} / 输入 ${tokenText(bucket.promptTokens)}`
    + `（${tokenText(bucket.callsWithKnownCache)} 笔报告了缓存）`;
  tr.append(key, calls, input, output, hit);
  return tr;
}

function renderUsageTable(id, rows) {
  const body = $(id);
  body.replaceChildren();
  if (!rows || rows.length === 0) {
    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.className = "empty-row";
    td.colSpan = 5;
    td.textContent = "暂无调用记录";
    tr.appendChild(td);
    body.appendChild(tr);
    return;
  }
  for (const row of rows) body.appendChild(usageBucketRow(row));
}

function renderUsageCalls() {
  const body = $("usage-calls");
  body.replaceChildren();
  if (!usageCalls.length) {
    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.className = "empty-row";
    td.colSpan = 7;
    td.textContent = "暂无调用记录";
    tr.appendChild(td);
    body.appendChild(tr);
  } else {
    for (const call of usageCalls) {
      const tr = document.createElement("tr");
      const time = document.createElement("td");
      time.className = "mono";
      time.textContent = shortTime(call.startedAt) || "未知时间";
      const purpose = document.createElement("td");
      purpose.textContent = call.purpose || "session";
      const target = document.createElement("td");
      target.className = "key";
      target.textContent = `${call.providerId || "?"} / ${call.modelId || "?"}`;
      const status = document.createElement("td");
      status.textContent = call.status || "未知";
      const input = document.createElement("td");
      input.className = "num";
      input.textContent = tokenText(call.usage?.inputTokens);
      const output = document.createElement("td");
      output.className = "num";
      output.textContent = tokenText(call.usage?.outputTokens);
      // 逐笔命中率：核心按**这一笔**算（UsageCall.CacheHitRate），不是从总量倒推。
      const hit = document.createElement("td");
      hit.className = "num";
      hit.textContent = rateText(call.cacheHitRate);
      hit.title = `命中 ${tokenText(call.cacheHitTokens)} / 输入 ${tokenText(call.promptTokens)}`;
      tr.append(time, purpose, target, status, input, output, hit);
      body.appendChild(tr);
    }
  }
  const total = usageSummary?.calls ?? 0;
  const first = total === 0 ? 0 : usageSkip + 1;
  const last = Math.min(usageSkip + usageCalls.length, total);
  $("usage-page-note").textContent = total ? `${first}-${last} / ${total}` : "0 次调用";
  $("usage-prev").disabled = usageSkip <= 0;
  $("usage-next").disabled = usageSkip + usageCalls.length >= total;
}

function renderUsageView() {
  const s = usageSummary;
  const box = $("usage-summary");
  box.replaceChildren();
  // ⚠️ 「输入」是**未命中输入**（`08` §3.1 的口径：它与「缓存读取」并列、不相交）。
  // 写成「输入 token」会被读成「这次请求的全部输入」，而那是两者之和 —— 差一个缓存读取的量。
  const stats = [
    ["调用次数", tokenText(s?.calls)],
    ["未命中输入 token", tokenText(s?.inputTokens)],
    ["输出 token", tokenText(s?.outputTokens)],
    ["缓存命中率", rateText(s?.cacheHitRate)],
    ["缓存命中 token", tokenText(s?.cacheHitTokens)],
    ["被缓存覆盖的输入", tokenText(s?.promptTokens)],
    ["缓存写入 token", tokenText(s?.cacheWriteTokens)],
    ["推理 token", tokenText(s?.reasoningTokens)],
    ["未知用量", tokenText(s?.callsWithUnknownUsage)],
    ["报告缓存的调用", s?.callsWithKnownCache === undefined || s?.calls === undefined
      ? "未报告"
      : `${tokenText(s.callsWithKnownCache)} / ${tokenText(s.calls)}`],
  ];
  for (const [label, value] of stats) {
    const stat = document.createElement("div");
    stat.className = "stat";
    const name = document.createElement("label");
    name.textContent = label;
    const number = document.createElement("strong");
    number.textContent = value;
    stat.append(name, number);
    box.appendChild(stat);
  }
  renderUsageTable("usage-providers", s?.byProvider);
  renderUsageTable("usage-models", s?.byModel);
  renderUsageCalls();
  $("usage-asof").textContent = s ? `更新于 ${shortTime(usageAsOf) || "刚刚"}` : "尚未加载";
}

async function loadUsageView(resetPage = false) {
  if (resetPage) usageSkip = 0;
  try {
    const [summaryResponse, callsResponse] = await Promise.all([
      fetch(`${core}/usage/summary`),
      fetch(`${core}/usage/calls?skip=${usageSkip}&limit=${usagePageSize}`),
    ]);
    if (!summaryResponse.ok || !callsResponse.ok) throw new Error("统计端点不可用");
    usageSummary = await summaryResponse.json();
    const page = await callsResponse.json();
    usageAsOf = page.asOf || null;
    usageCalls = Array.isArray(page.calls) ? page.calls : [];
    renderUsageView();
  } catch (e) {
    usageSummary = null;
    usageCalls = [];
    usageAsOf = null;
    renderUsageView();
    renderMeta("✗ 统计读取失败：" + (e && e.message ? e.message : e), "error");
  }
}

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
  await loadSchema();
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
  // ⚠️ **一个端点拿全**（`GET /sessions`，2026-08 询问批加的）：它一次给出每条的
  // 运行状态与待办询问。旧写法是 `/projects` + 每个 pk 的 `/projects/{pk}/sessions`
  // + **每个会话一次** `/sessions/{id}/state`——N+1 次往返，而这个列表是**每次
  // 事件都要重画**的。⚠️ 旧写法里那个 `/state` 还**有副作用**（核心会 Activate），
  // 新端点刻意不载入（见核心里那段注释）。
  const rows = await (await fetch(core + "/sessions")).json();
  sessions.clear();
  projects.clear();
  for (const r of rows) {
    if (currentProject === null) currentProject = r.projectKey;
    sessions.set(r.sessionId, { projectKey: r.projectKey, parentSessionId: r.parentSessionId, ...r });

    const p = projects.get(r.projectKey) || { count: 0, dir: null, asks: 0 };
    p.count += 1;
    // ⚠️ 判据是 `ask?.askId`，**不是** `ask`：没在等时核心交的是 `{}`，
    // 而 `{}` 在 JS 里是 truthy（见核心那段的注释）。
    if (r.ask?.askId) p.asks += 1;
    projects.set(r.projectKey, p);

    // ⚠️ **`spec` 的形状不能改**：`renderSessions` 读 `spec.title` / `spec.archivedAt`，
    // 而这两件事在这个端点里是**顶级字段**。在这里补一次映射，
    // 比让两个渲染点各判一次「这份数据是从哪来的」要好（两处判据必漂）。
    info.set(r.sessionId, {
      spec: { title: r.title, archivedAt: null, projectKey: r.projectKey },
      turn: r.turn,
      status: r.status,
      ask: r.ask,
    });
  }

  renderSessions();

  // 在这里补订项目流。重复订阅是安全的：核心对同 topic 换掉旧的，
  // 且 since: null 只要从现在起的事件 ⇒ 任一顺序下恰剩一条有效订阅。
  if (currentProject) subscribe(`project:${currentProject}`, null);
  // ⚠️ **审批流**（`03` §5 那条「头最需要的」）：一次订阅看见**所有**会话的待审批。
  // 不订它的话，别的会话在等人，而当前页面**完全看不出来**。
  subscribe("approvals", null);
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

    // 归档的**默认折叠**（见 `showArchived` 的注释：过滤只发生在这个显示层）。
    const archived = ids.filter((id) => (info.get(id)?.spec || {}).archivedAt);
    const visible = showArchived ? ids : ids.filter((id) => !archived.includes(id));

    // 计数只说**看得见的那部分**，否则「折叠着却显示 5」是个谎。
    cnt.textContent = visible.length;
    head.append(nm, key, cnt);

    // ⚠️ **子会话挂在父后面**（2026-10-24）：`parentSessionId` 是 spec 里的**出生事实**，
    // 而 `ListSessions` 按 `CreatedAt` **降序**排（新的在前）⇒ 子会话（更晚创建）
    // 会排在父**前面**。这里按「父 → 它的孩子们」重排一次。
    // ⚠️ **只认「父也在这一栏里」**：父被删掉的孤儿子照顶层画（它的 `depth` 是 0），
    // 而不是画成一个缩进着、却指不到谁的孤儿。
    // ⚠️ **顺序是确定的**：`visible` 本身有序（`ListSessions` 的既有次序），
    // 而这里只做一次稳定的深度优先展开，不重新排序。
    const kids = new Map();
    const roots = [];
    for (const id of visible) {
      const pid = (sessions.get(id) || {}).parentSessionId;
      if (pid && visible.includes(pid)) {
        if (!kids.has(pid)) kids.set(pid, []);
        kids.get(pid).push(id);
      } else {
        roots.push(id);
      }
    }
    const ordered = [];
    const depth = new Map();
    const walk = (id, d) => {
      ordered.push(id);
      depth.set(id, d);
      for (const c of kids.get(id) || []) walk(c, d + 1);
    };
    for (const r of roots) walk(r, 0);

    if (archived.length) {
      const toggle = document.createElement("button");
      toggle.className = "archtoggle";
      toggle.textContent = showArchived ? `隐藏归档 (${archived.length})` : `显示归档 (${archived.length})`;
      toggle.onclick = (e) => { e.stopPropagation(); showArchived = !showArchived; renderSessions(); };
      head.appendChild(toggle);
    }
    g.appendChild(head);

    for (const id of ordered) {
      const v = info.get(id) || {};

      const row = document.createElement("div");
      row.className = "srow" + (id === current ? " active" : "") + (v.spec?.archivedAt ? " archived" : "");
      row.title = id + (v.status ? ` · ${v.status}` : "");
      if (v.spec?.archivedAt) row.title += ` · 已归档 ${shortTime(v.spec.archivedAt)}`;

      // ⚠️ **子会话缩进一格**（2026-10-24）：判据是 `depth`（重排时算出来的），
      // 而它来自 spec 里那个**出生事实** `parentSessionId`——父被删掉的孤儿子
      // 在这里 depth 是 0（`walk` 只从根走），于是它**照顶层画**，不会凭空缩进。
      if (depth.get(id)) {
        row.classList.add("child");
        row.style.paddingLeft = `${depth.get(id) * 1.2}em`;
        row.title += ` · 子会话（父 ${(sessions.get(id) || {}).parentSessionId || ""}）`;
      }

      // 刻度：横杠的高度 = 这一步数 / 本栏最大步数。
      const gauge = document.createElement("span");
      gauge.className = "gauge";
      gauge.style.setProperty("--t", v.turn || 0);
      gauge.style.setProperty("--max", maxTurn);

      const sid = document.createElement("span");
      sid.className = "sid";
      // 有名字就显示名字（id 在 hover 的 title 里）；没名字还是显示 id。
      // ⚠️ 名字只在 `session.create` 那一次定，**没有任何命令能改它**。
      const spec = v.spec || {};
      if (spec.title) {
        sid.textContent = spec.title;
        sid.classList.add("named");
      } else {
        sid.textContent = id.slice(0, 8);
      }

      const turn = document.createElement("span");
      turn.className = "sturn";
      turn.textContent = v.turn === undefined ? "·" : `#${v.turn}`;

      row.append(gauge, sid, turn);

      // ⚠️ **「这个会话在等我」必须看得见**（`01` §3.8）：一条只有点进去才知道的
      // 待审批，等于没有。判据是 `ask?.askId`——没在等时核心交 `{}`，而 `{}` 是 truthy。
      //
      // ⚠️ **`kind` 是小写 `"permission"`**（2026-10-11 改）。这里曾经拿 PascalCase 去比：
      // 那时核心在 `/sessions` 上手写 `.ToString()`（PascalCase），而同一份数据在
      // `/state` 里走 `JsonStringEnumConverter(camelCase)` ⇒ **同一条响应两种拼法**。
      // 核心已统一成 camelCase（同 `"event"` / `"toolUse"` 那些线上枚举值），
      // 拿旧拼法去比**不会报错**，只会静默把「？授权」画成「？提问」。
      if (v.ask?.askId) {
        const badge = document.createElement("span");
        badge.className = "askbadge";
        badge.textContent = v.ask.kind === "permission" ? "？授权" : "？提问";
        badge.title = "这个会话在等你回答" + (v.ask.deadlineAt ? `（${shortTime(v.ask.deadlineAt)} 之前）` : "");
        row.appendChild(badge);
        row.classList.add("waiting");
      }

      row.onclick = () => openSession(id);
      g.appendChild(row);
    }
    box.appendChild(g);
  }
}

// ---- 打开会话：先快照，再带高水位订阅 ----

/// <summary>
/// 打开一个会话——**它保证不抛**。
///
/// <para>⚠️ **这层壳是实测加的**（2026-10-06）：它从**五处**被调用
/// （点会话行 · 引导 · 重连 · 视图切回 · `create` 之后），其中几处**没有 catch**
/// ⇒ 里面任何一步抛出去都是一次**未观测的 promise 拒绝**，页面**一声不吭**
/// （实测症状：点会话行之后时间线空着，除了浏览器控制台没有任何提示——
/// 根因是 `renderAxes` 换掉了带 id 的节点，而它在**清空时间线之后、渲染历史之前**）。</para>
///
/// <para>⚠️ 所以这里不只是「接住」：**要把失败说到时间线上**。核心那条
/// 「不许静默」的纪律在这里同样适用——用户看不到的错误等于没有报错。</para>
/// </summary>
async function openSession(id) {
  try {
    await openSessionInner(id);
  } catch (e) {
    console.error("[lilara] openSession 失败", id, e);
    renderMeta("✗ 打开会话时出错：" + (e && e.message ? e.message : e), "error");
  }
}

async function openSessionInner(id) {
  // 世代号让后一次调用作废前一次（同 id 双击时 current !== id 判不出来）。
  const gen = ++epoch;

  // ⚠️ **记下「来的时候开的是哪一个」**：下面决定要不要先清空中栏要用它
  //（`current` 马上就会被改成 `id`，那时「是不是换了会话」就再也判不出来了）。
  const leaving = current;

  // ⚠️ **先摘掉上一条会话流**（2026-10-06 加）：核心的订阅是**按 topic** 一条订阅 + 一条泵，
  // 重复订同一个会换掉旧的，但**换到别的会话时不换** ⇒ 旧会话的泵一直活着、事件一直发过来，
  // 而这头会丢弃它们 ⇒ 白占一条泵与整条连接，切得越多积得越多。
  if (current && current !== id) unsubscribe(`session:${current}`);

  // subscribe() 在 !wsReady 时会静默返回 ⇒ 先等连接就绪，
  // 否则会话流可能根本没订上，而页面以为订过了。
  await connected();
  if (gen !== epoch) return;
  current = id;
  if (leaving !== id) renderContextStatus(null);
  $("interrupt").disabled = false;   // 没选中会话时它是死的 —— 别让它看起来可点（点了也没事，只是会骗人）
  $("gc").disabled = false;
  renderSessions();
  // ⚠️ 这里**不设 `#title`**——名字/ id 要等快照回来（`renderTitle`）。先设一个 id
  // 再被名字盖掉会闪一下，而且那是同一事实的两个显示点。

  // ⚠️ **只有「换了会话」才清空中栏**（2026-10-06 改，原来是无条件清）。
  //
  // 这个函数有**两个**入口：切会话（用户点的），以及**重拉当前会话**
  //（`session.reclaimed` / `spec.changed` 那两个回声走的就是这条）。
  // 无条件清空时，第二条路会把**别处刚画上去的行一起抹掉**——回收那条正是这么消失的：
  // 回执先到、往时间线上画一行「回收了 N 条结果」，`session.reclaimed` 后到、
  // 重拉、清空 ⇒ **用户一个字都看不到**，与「坏了」分不开。
  //
  // 两条路的要求其实是相反的：换会话时那一屏**必须**清（停在别人的时间线上是本页
  // 能犯的最大的谎）；重拉当前会话时**不能**清——`replaceChildren` 会在快照回来后
  // 整体换掉，中间那一瞬留着旧内容，比留一片空白更诚实（也不闪）。
  if (leaving !== id) $("timeline").innerHTML = "";

  // ⚠️ 两条 fetch 必须**各自**带 catch：`session.delete` 会让它们 404，
  // 而 404 的 `.json()` 会**抛**。不接的话这个 async 函数变成一次未观测拒绝 ⇒
  // 页面停在「current 已经指向一个不存在的会话、时间线却被清空了」那个半截状态。
  let state, history;
  try {
    state = await (await fetch(`${core}/sessions/${id}/state`)).json();
    history = await (await fetch(`${core}/sessions/${id}/history?limit=50`)).json();
  } catch (e) {
    if (gen !== epoch) return;
    // 没东西可画 ⇒ 退回「没选会话」，别留一个空壳。
    clearCenter();
    renderMeta("✗ 会话读不出来（可能刚被删掉）：" + (e && e.message ? e.message : e), "error");
    // ⚠️ 再打一行**给人看**的：上面那句在时间线里，而时间线此刻刚被 `clearCenter` 清空——
    // 用户看到的可能只是「空白」。控制台这份是排查用的第二只眼睛。
    console.error("[lilara] openSession 失败", id, e);
    return;
  }

  // 快照回来时用户可能已开了别的会话——这些是全局节点，继续渲染会把上一次的
  // turn 追加进已经清空的那条时间线里。
  if (gen !== epoch) return;

  // 派生缓存与面板都更新（供左栏刻度与右栏三块用）。
  info.set(id, { spec: state.spec, turn: state.state.turn, status: state.state.status });
  const p = projects.get(state.spec.projectKey);
  if (p && !p.dir) p.dir = state.spec.workingDirectory;

  renderSlots(state.slots);
  if (state.axisDefinitions) AXES = state.axisDefinitions;
  renderAxes(state.axes);
  if (state.slots?.problem) renderMeta(state.slots.problem, "error");
  renderAsk(state);
  renderTitle(state.spec);
  renderSessionPanel(state);
  renderProjectPanel(state);
  setStatus(state.state.status, state.state.turn);
  loadSessionContext(id);
  renderSessions();
  // ⚠️ **整体换掉，不是逐条追加**（2026-10-06 改）。这个函数是**重拉**（「把现在这份画出来」），
  // 而重拉期间时间线上可能已经有东西了——`session.reclaimed` 的处理器刚把「回收了 N 条结果」
  // 画上去，回收条数那个回声就是这么丢的（见 openSession 里那段）。逐条追加会把旧内容
  // **留在上面、叠成两遍**。
  // ⚠️ 用 `replaceChildren`（不是 `innerHTML = ""` 再逐条）：一条语句换完，中间不留空白帧。
  $("timeline").replaceChildren();
  renderHistoryPage(history, false);

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

/// **退订一个会话流**——`openSession` 切走时调。
///
/// <para>⚠️ <b>不退订会造成积压</b>（2026-10-06 查出来的）：核心的 `subs` 是**按 topic**
/// 建的一条订阅 + 一条泵，**重复订阅同一个 topic 会换掉旧的**（那边注释写了为什么），
/// 但**换到别的会话时不换**——旧会话的泵一直活着，把那个会话的事件继续发过来，
/// 而头会丢弃它们（`p.sessionId !== current`）⇒ <b>白占一条泵与整条连接</b>，
/// 切得越多积得越多。</para>
function unsubscribe(topic) {
  if (!wsReady) return;
  ws.send(JSON.stringify({ kind: "unsubscribe", topic }));
}

// ---- 渲染：核心渲染，头只显示 ----

// ⚠️ 抽文本这一步**不在**核心的读取点里（`SessionQueries` 只有 ModelBody / UserBody），
//    所以这里是**手抄的第二副本**（见 `实现现状.md` §四）。改核心的措辞要同时改这里。
const EMPTY_BODY = "（这一步没有正文输出）";

/// **一串块** → 一段文本（只取 `text` 块，其余丢掉）。
///
/// <para>⚠️ <b>它的入参必须是一串块。</b>本仓栽过两次同一形状的错——都是「把不是一串块的东西
/// 喂进来」，而两次<b>都不报错</b>：</para>
/// <list type="bullet">
///   <item><b>喂一个（块）对象</b> ⇒ <c>b.filter is not a function</c> ⇒ 打开会话时整段渲染中断，
///   中栏连**历史都画不出来**（用户报的那句 `(blocks || []).filter is not a function`）。</item>
///   <item><b>喂那个槽信封</b>（<c>{system, premier, floating}</c>）或一个字符串 ⇒
///   `filter` 直接跳过全部 ⇒ 空串 ⇒ 每个槽都显示「（空）」，<b>静默</b>。</item>
/// </list>
/// <para>⇒ 拿不准就先问「我手上到底是一串块吗」，别直接喂。见 <c>HeadScriptTests</c> 的三条守卫。</para>
function textOf(blocks) {
  return (blocks || []).filter((b) => b.kind === "text").map((b) => b.text).join("");
}

/// **一个槽**（`/slots` 的 `system` / `premier` / `floating`）→ 显示什么。
///
/// <para>⚠️ <b>抽文本走 <see cref="textOf"/>，本函数只负责「空」那一格</b>——
/// 块级逻辑只有一份实现（两处各写一遍的话，改一处忘一处是静默的：显示成「（空）」）。</para>
///
/// <para>⚠️ <b>入参必须是一串<b>块</b>，不是槽对象本身</b>：
/// `renderSlots` 传的是 `slots.system` 这些数组。传错一层的话 `b.kind` 全是 `undefined`
/// ⇒ 拼出来是空串 ⇒ 每个槽都显示「（空）」，而<b>不报错</b>。</para>
function slotText(blocks) {
  if (!blocks || !blocks.length) return "（空）";
  return textOf(blocks);
}

function renderHistoryPage(page, prepend) {
  historyState.id = current;
  historyState.cursor = page.nextCursor || null;
  historyState.more = !!page.hasMore;
  const items = page.rendered || [];
  const timeline = $("timeline");
  const fragment = document.createDocumentFragment();
  for (const item of items) {
    if (!item.id || timeline.querySelector(`[data-history-id="${CSS.escape(item.id)}"]`)) continue;
    fragment.appendChild(createHistoryItem(item));
  }
  if (prepend) {
    const first = [...timeline.children].find((el) => el.id !== "history-more");
    if (first) timeline.insertBefore(fragment, first);
    else timeline.appendChild(fragment);
  } else timeline.appendChild(fragment);
  renderHistoryMore();
}

function renderHistoryMore() {
  let bar = $("history-more");
  if (!bar) {
    bar = document.createElement("div");
    bar.id = "history-more";
    $("timeline").prepend(bar);
  }
  bar.replaceChildren();
  if (!historyState.more) return;
  const button = btn("加载更早记录", loadOlderHistory);
  button.disabled = historyState.loading;
  bar.appendChild(button);
}

async function loadOlderHistory() {
  if (historyState.loading || !historyState.more || !historyState.cursor || !current) return;
  historyState.loading = true;
  renderHistoryMore();
  const id = current;
  try {
    const r = await fetch(`${core}/sessions/${id}/history?limit=50&before=${encodeURIComponent(historyState.cursor)}`);
    const page = await r.json();
    if (!r.ok) throw new Error(page.error || "历史分页失败");
    if (id === current) renderHistoryPage(page, true);
  } catch (e) {
    if (id === current) renderMeta("✗ 加载更早记录失败：" + (e.message || e), "error");
  } finally {
    historyState.loading = false;
    if (id === current) renderHistoryMore();
  }
}

function renderTurn(from, blocks, cls, historyId) {
  const item = { kind: "message", id: historyId || `live-${Date.now()}-${Math.random()}`, from, blocks };
  renderHistoryItem(item, cls);
}

function detail(label, text, cls) {
  const el = document.createElement("details");
  el.className = "detail " + (cls || "");
  const summary = document.createElement("summary");
  summary.textContent = label;
  const content = document.createElement("div");
  content.className = "detail-body";
  content.textContent = text || "（空）";
  el.append(summary, content);
  return el;
}

function blocksText(blocks) {
  return (blocks || []).map((b) => b.kind === "text" ? b.text
    : b.kind === "store" ? `（内容在库里 ${(b.sha256 || "").slice(0, 8)}…）`
      : b.kind === "image" ? `（图片 ${(b.sha256 || "").slice(0, 8)}…）`
        : b.kind === "reasoning" ? (b.text || "（内容已隐藏）") : `（${b.kind}）`).join("\n");
}

function createHistoryItem(t, cls) {
  // 线：一条横贯的分隔（与核心的 `ReleaseEvent`、控制台 `history` 同形）。
  // ⚠️ 它只能来自 `/history` 的分页数据——模型自己的上下文里没有这条记录。
  if (t.kind === "release") {
    const line = document.createElement("div");
    line.className = "turn release-line";
    if (t.id) line.dataset.historyId = t.id;
    const scope = (t.scope || []).join("、");
    const summary = t.summary ? `（${t.summary}）` : "";
    line.textContent = `── ✂ 释放 ${scope || "（未给范围）"}${summary} ──`;
    return line;
  }

  const wrap = document.createElement("div");
  if (t.id) wrap.dataset.historyId = t.id;
  wrap.className = "turn " + (cls || (t.kind === "toolResult" ? "meta" : t.from));
  const who = document.createElement("div");
  who.className = "who";
  who.textContent = t.kind === "toolResult" ? "·" : (t.from === "user" ? "你" : "⏺");
  const body = document.createElement("div");
  body.className = "body";

  if (t.kind === "toolResult") {
    // ⚠️ 独立一行时**不要 `white-space: pre-wrap`**：`body.plain` 让折叠项自己撑开
    // （折叠项内部那份已经设了 pre-wrap），否则外层会把缩进也画出来。
    body.classList.add("plain");
    body.appendChild(detail(`工具结果${t.callId ? ` · ${t.callId}` : ""}`, blocksText(t.blocks), "result"));
  } else {
    // ⚠️ **顺序是 思考 → 正文 → 其余块**（2026-10-17 用户裁定）。原先按块的原序输出，
    // 于是「思考、正文、调用」里的思考落在正文**上面**、而「正文、思考」里落在下面——
    // 记录里的块序是模型给的原样（不改），但**人读的那一栏**统一成「先想、再说、再调」。
    // 正文用 textContent 原样渲染；思考 / 调用 / 其他块各是一个折叠项。
    //
    // ⚠️ **工具结果按 `callId` 配到对应的调用下面**，不再各自单列一行
    // （2026-10-17）：实时事件与 `/history` 都按「模型消息之后跟它的结果」到达，
    // 配对由这里做——`createHistoryItem` 是纯函数，配对表由调用方传进来。
    const reasoning = (t.blocks || []).filter((b) => b.kind === "reasoning");
    const text = (t.blocks || []).filter((b) => b.kind === "text").map((b) => b.text).join("");
    const others = (t.blocks || []).filter((b) => !["text", "reasoning"].includes(b.kind));

    for (const b of reasoning) body.appendChild(reasoningDetail(b));

    const textEl = document.createElement("div");
    textEl.className = "msg-text";
    // ⚠️ 占位与 `EMPTY_BODY` 同一句话（`05` §4：空正文必须显式可见）。
    // 只有推理、没有正文时也显示占位——「这一步没有正文输出」仍然是事实。
    textEl.textContent = text || (others.length ? "" : EMPTY_BODY);
    if (textEl.textContent) body.appendChild(textEl);

    for (const b of others) installToolRow(body, t, b);
  }
  wrap.append(who, body);
  return wrap;
}

function reasoningDetail(b) {
  const redacted = b.reasoningKind === "redacted";
  return detail(redacted ? "思考过程（已隐藏）" : "思考过程",
    redacted ? "内容已隐藏" : b.text, "reasoning");
}


/// **一个非文本、非推理的块 → 一行**。今天的「其余块」里只有 `toolUse` 是活的；
/// 别的（`image` / `store`）照旧给一个地址式的折叠项，并**保留调用行的 id**，
/// 好让结果配得上来（`data-call-id`）。
function installToolRow(body, t, b) {
  if (b.kind === "toolUse") {
    const row = detail(`调用 ${b.name}`, b.arguments, "tool");
    // ⚠️ 结果配对的锚——`t` 的 `callId` 由 `message.appended` / `/history` 原样带出。
    if (b.callId) row.dataset.callId = b.callId;
    body.appendChild(row);
    return;
  }
  body.appendChild(detail(`内容（${b.kind}）`, blocksText([b]), "tool"));
}

function renderHistoryItem(t, cls) {
  if (t.id && document.querySelector(`[data-history-id="${CSS.escape(t.id)}"]`)) return;
  const el = createHistoryItem(t, cls);
  appendTimeline(el);
}

function appendToolResult(t) {
  const row = detail(`工具结果${t.callId ? ` · ${t.callId}` : ""}`, blocksText(t.blocks), "result");
  if (t.id) row.dataset.historyId = t.id;
  if (t.callId) row.dataset.resultCallId = t.callId;

  // 配到对应的调用下面（`[data-call-id]` 是 `installToolRow` 留的锚）。
  // ⚠️ 找不到就不硬塞：它可能属于更早的一条模型消息，而那条**还没被加载**
  //（分页）——这时独立显示，重拉后自然归位（`renderHistoryPage` 会整段重画）。
  const anchor = t.callId
    ? document.querySelector(`[data-call-id="${CSS.escape(t.callId)}"]`)
    : null;
  if (anchor) {
    anchor.after(row);
    // ⚠️ 调用项自己也是 `<details>`：结果跟在它**同级**时，折叠 / 展开互不牵连。
  } else {
    appendTimeline(row);
  }
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
  // ⚠️ **三格从 2026-10-11 起都是**真的**了**（提示词拼装批）。在那之前只有系统那格是真的
  // （2026-10-07 预设批），另外两格恒交空数组、页面写着「尚未实现」。
  // ⇒ 显示「（空）」现在三格的含义**各自明确**，都不再意味着「没实现」：
  //   系统 —— 这个会话选了空预设 / 纯 chat（`presets/<id>.md` 是个空文件）
  //   首要 —— 没配过 `<数据根>/premiers/<id>.md`（核心**不替用户编人设**）
  //   浮动 —— 核心每步现算的轴表；空 = 一条轴都没进上下文（罕见，但判定上是合法的）
  // ⚠️ **这正是义务 1 要的东西**（`05` §4）：两块的**有无**要并排看得见，
  // 而「看得见空」不等于「看得见一个空壳」——见 `slotText` 那条契约。

  // ⚠️ 抽文本走 `slotText`，**不用 `textOf`**：后者的契约是「一串块」，
  // 而这里手上是「一个槽的块列表」（不是块、也不是槽信封）。
  const put = (el, x) => {
    const v = slots ? slotText(x) : "（空）";
    el.textContent = v;
    el.classList.toggle("empty", v === "（空）");
  };
  put($("slot-system"), slots && slots.system);
  put($("slot-premier"), slots && slots.premier);
  // ⚠️ 浮动槽与上面两格**走同一个助手、同一个判空形状**——三格是同一类东西，
  // 各写一套的话「哪一格忘了判空」会变成一个只在空槽下才出现的 TypeError。
  put($("floating-body"), slots && slots.floating);
}

function setStatus(status, turn, note) {
  const el = $("status");
  // ⚠️ `turn` 可以是 undefined（调用方拿不到步数时不编一个——那会印出「第 undefined 步」）。
  //
  // ⚠️ **附注（note）的存活规则**：只有 `retry.attempt` 会设它；`Running` 期间的其它状态更新
  // （`step.begin`）与**任何离开 `Running` 的迁移**（`step.end` / `state` / 打开会话）都把它清掉。
  // ⚠️ 别改成「每次调用都把上一条 note 传下来」：那样下一个事件来临之前，状态条上会一直挂着
  // 上一句「重试」——而它描述的等待已经结束了。
  statusNote = status === "Running" ? (note ?? null) : null;

  let text = turn === undefined || turn === null ? String(status) : `${status} · 第 ${turn} 步`;
  if (statusNote) text += ` · ${statusNote}`;
  el.textContent = text;
  el.className = "chip " + (status === "Running" ? "running" : "live");
}

// ---- 视图切换：会话 / 供应商 / 统计 ----
// ⚠️ 只在**页面内**切：共享同一个 WS、同一份 providers 缓存、同一套样式。
// 另开一个 HTML 文件就得把 WS 那套管线抽成共用 js 或重写一遍，而这两个视图用的
// 就是同一份数据（会话视图的供应商下拉 = 供应商视图维护的那份清单）。

function showView(which) {
  document.body.classList.toggle("providers", which === "providers");
  document.body.classList.toggle("usage", which === "usage");
  $("nav-sessions").classList.toggle("on", which === "sessions");
  $("nav-providers").classList.toggle("on", which === "providers");
  $("nav-usage").classList.toggle("on", which === "usage");
  // 视图进 URL 片段：可收藏、可前进后退、刷新后停在原地。
  // ⚠️ 用 `history.replaceState` 而不是改 `location.hash`——后者会**再触发**一次 hashchange，
  // 与我们这里读 hash 的地方来回打架（无限循环）。
  try { history.replaceState(null, "", which === "providers" ? "#providers" : which === "usage" ? "#usage" : "#sessions"); } catch { }
  if (which === "providers") {
    loadProviders().then(renderProvidersView).catch((e) => renderMeta("✗ " + e.message, "error"));
  } else if (which === "usage") {
    loadUsageView(true);
  } else {
    // 切回来时把会话视图的缓存刷一遍——供应商可能在那边被改过。
    renderSessions();
    if (current) openSession(current);
  }
}

function renderProvidersView() {
  const grid = $("prov-grid");
  grid.innerHTML = "";
  $("prov-count").textContent = providers.length ? `共 ${providers.length}` : "";

  for (const p of providers) grid.appendChild(providerCard(p));

  // 「新建」永远在最后。⚠️ 默认**收起**：它是个入口不是数据，一进页面就摊开一张
  // 空表单会把「我有哪些供应商」这件事淹没掉。
  grid.appendChild(showNewCard ? providerCard(null) : newProviderStub());
}

function newProviderStub() {
  const card = document.createElement("div");
  card.className = "pcard";
  card.style.cssText = "border-style:dashed;text-align:center";
  const b = btn("+ 新建供应商", () => { showNewCard = true; renderProvidersView(); }, "btn-primary");
  card.appendChild(b);
  return card;
}

// `p === null` ⇒ 新建卡。
function providerCard(p) {
  const isNew = p === null;
  const editing = isNew || editingProvider === p.id;

  const card = document.createElement("div");
  card.className = "pcard" + (editing ? " editing" : "");

  // ---- 只读态：一行摘要 + 三个动作 ----
  if (!editing) {
    const top = document.createElement("div");
    top.className = "top";
    const id = document.createElement("span");
    id.className = "pid";
    id.textContent = p.id;
    top.appendChild(id);
    card.appendChild(top);

    const dl = document.createElement("dl");
    dl.className = "kv";
    kv(dl, [
      ["协议", p.protocol],
      ["默认模型", p.defaultModel],
      ["模型清单", p.models && p.models.length ? p.models.join("、") : "(未声明)", !p.models || !p.models.length],
      ["端点", p.endpoint],
      ["认证", p.authScheme],
      // 一次模型调用的超时：没配 = 由协议自己的默认值管（OpenAI 100 秒 / Claude 由 SDK 定）。
      ["调用超时", p.timeoutSeconds ? `${p.timeoutSeconds} 秒` : "(默认)"],
      ["密钥", p.hasApiKey ? "已配置" : "未配置", !p.hasApiKey],
    ]);
    card.appendChild(dl);

    const actions = document.createElement("div");
    actions.className = "actions";
    actions.append(
      btn("编辑", () => { editingProvider = p.id; renderProvidersView(); }),
      btn("自检", () => testProvider(p.id)),
      btn("删除", () => deleteProvider(p.id), "btn-ghost"),
    );
    card.appendChild(actions);

    const note = provNotes.get(p.id);
    if (note) {
      const n = document.createElement("div");
      n.className = note.ok === false ? "note-bad" : "note-ok";
      n.textContent = note.text;
      card.appendChild(n);
    }
    return card;
  }

  // ---- 编辑态（新建走同一张表单，只是 id 可填、没有删除） ----
  const form = document.createElement("div");
  form.className = "form";

  const fId = field("id", "id", isNew ? "" : p.id, "mono");
  if (!isNew) { fId.input.disabled = true; fId.input.title = "id 是文件名，改它等于改名——先删了重建"; }
  fId.input.placeholder = "单个文件名，如 local";

  const fProtocol = selectField("protocol", "协议", SCHEMA.protocols, isNew ? SCHEMA.protocols[0] : p.protocol);
  const fModel = field("defaultModel", "默认模型", isNew ? "" : p.defaultModel);
  const fModels = field("models", "模型清单（逗号分隔）",
    isNew ? "" : (p.models || []).join(","), "mono");
  fModels.input.placeholder = "deepseek-flash,gpt-4o（留空 = 不声明，头退回文本框）";
  // 一次模型调用的超时（秒）。⚠️ 留空 = 「没配」= **不发这个键**（= 保留旧值），
  // 与密钥同一条语义——别发一个 `null` 或 `0` 去表达「默认」，那会被读成一次真的配置。
  const fTimeout = field("timeout", "调用超时（秒）",
    isNew || !p.timeoutSeconds ? "" : String(p.timeoutSeconds), "mono");
  fTimeout.input.type = "number";
  fTimeout.input.min = "1";
  fTimeout.input.placeholder = "留空 = 协议默认（OpenAI 100 秒；Claude 由 SDK 定）";
  const fEndpoint = field("endpoint", "端点", isNew ? "" : (p.endpoint || ""), "mono");
  fEndpoint.input.placeholder = "留空 = 官方地址";
  const fAuth = selectField("auth", "认证方式", SCHEMA.authSchemes,
    isNew ? "" : (p.authScheme || ""), /* allowEmpty */ true);
  const fKey = field("apiKey", "密钥", "", "mono");
  fKey.input.type = "password";
  // ⚠️ 绝不回填密钥（key 能改不能看）。留空 = 保留旧的。
  fKey.input.placeholder = p && p.hasApiKey ? "（已配置，留空就不改）" : "新建必须填";

  // 顺序：id / 协议 / 默认模型 / 认证 / 端点 / 清单。id 与协议并排，其余各占一行。
  form.append(fId.wrap, fProtocol.wrap);
  form.append(fModel.wrap, fAuth.wrap);
  form.append(fEndpoint.wrap);
  form.append(fModels.wrap);
  form.append(fTimeout.wrap);
  form.append(fKey.wrap);
  card.appendChild(form);

  const actions = document.createElement("div");
  actions.className = "actions";
  actions.append(
    btn("保存", async () => {
      const payload = {
        kind: "provider.upsert",
        id: fId.input.value.trim(),
        protocol: fProtocol.select.value || null,
        model: fModel.input.value.trim() || null,
        endpoint: fEndpoint.input.value.trim() || null,
        auth: fAuth.select.value || null,
        // ⚠️ 清单用**数组**发（核心那条 `OptStringList` 只认数组）。
        // 空串 ⇒ 空数组 = 明确清空；这不是「没给」（没给是**不发这个键**）。
        models: fModels.input.value.trim() === ""
          ? []
          : fModels.input.value.split(",").map((s) => s.trim()).filter(Boolean),
      };
      // ⚠️ 密钥留空 ⇒ **整个键都不发**（= 保留旧 key），不是发一个空串。
      const key = fKey.input.value;
      if (key !== "") payload.apiKey = key;

      // 超时同一条语义：留空 ⇒ 不发这个键（= 保留旧值）。
      // ⚠️ 这里先判「是不是整数」：核心把非数字读成「没给」，于是 `30s` 会被静默当成
      // 「保留旧值」并回 ✓ —— 用户以为改了，实际的数字谁也没见过。
      const timeoutRaw = fTimeout.input.value.trim();
      if (timeoutRaw !== "") {
        const seconds = Number(timeoutRaw);
        if (!Number.isInteger(seconds)) {
          showCardError(card, "调用超时要是整数秒（留空 = 用协议默认）。");
          return;
        }
        payload.timeoutSeconds = seconds;
      }

      const r = await commandNow(crypto.randomUUID().slice(0, 8), payload);
      if (!r.accepted) { showCardError(card, r.reason); return; }
      editingProvider = null;
      showNewCard = false;
      await loadProviders();
      renderProvidersView();
      refreshProviderChoices();
    }),
    btn("取消", () => { editingProvider = null; showNewCard = false; renderProvidersView(); }),
  );
  if (!isNew) actions.appendChild(btn("删除", () => deleteProvider(p.id), "btn-ghost"));
  card.appendChild(actions);

  form.querySelectorAll("input,select")[0]?.focus();
  return card;
}

function field(name, label, value, cls) {
  const wrap = document.createElement("div");
  wrap.className = "field";
  const lab = document.createElement("label");
  lab.textContent = label;
  const input = document.createElement("input");
  input.className = cls || "";
  input.value = value || "";
  lab.htmlFor = input.id = "pf-" + name;
  wrap.append(lab, input);
  return { wrap, input };
}

function selectField(name, label, options, value, allowEmpty) {
  const wrap = document.createElement("div");
  wrap.className = "field";
  const lab = document.createElement("label");
  lab.textContent = label;
  const sel = document.createElement("select");
  sel.id = "pf-" + name;
  if (allowEmpty) {
    const o = document.createElement("option");
    o.value = ""; o.textContent = "（默认）";
    sel.appendChild(o);
  }
  for (const v of options || []) {
    const o = document.createElement("option");
    o.value = v; o.textContent = v;
    if (value === v) o.selected = true;
    sel.appendChild(o);
  }
  wrap.append(lab, sel);
  return { wrap, select: sel };
}

function btn(label, onclick, cls) {
  const b = document.createElement("button");
  b.textContent = label;
  if (cls) b.className = cls;
  b.onclick = onclick;
  return b;
}

function showCardError(card, reason) {
  const n = document.createElement("div");
  n.className = "note-bad";
  n.textContent = "✗ " + reason;
  card.appendChild(n);
}

async function deleteProvider(id) {
  if (!confirm(`删除供应商 '${id}'？\n\n引用它的会话不会因此改绑——它们会立刻连不上，\n而会话视图里可以把它们改到别的供应商。`)) return;
  const r = await commandNow(crypto.randomUUID().slice(0, 8), { kind: "provider.delete", id });
  if (!r.accepted) { alert(r.reason); return; }
  await loadProviders();
  renderProvidersView();
  refreshProviderChoices();
}

function testProvider(id) {
  const cid = crypto.randomUUID().slice(0, 8);
  testCmd.set(cid, id);
  provNotes.set(id, { text: "正在测…", ok: null });
  renderProvidersView();
  command(cid, { kind: "provider.test", id });
}

// ---- 会话视图里的「可改」下拉 ----
// 改 provider / model 都发 `session.configure`（一条命令，两个可空字段）。
// 改完**不**本地改显示：等核心的 `spec.changed` 回来再渲染——那是真相。
// （否则核心若拒绝（比如供应商不存在），页面会先显示成功再被打脸。）

function providerSelect(currentId, onPick) {
  const sel = document.createElement("select");
  for (const p of providers) {
    const o = document.createElement("option");
    o.value = p.id; o.textContent = p.id;
    if (p.id === currentId) o.selected = true;
    sel.appendChild(o);
  }
  // 当前绑的那个可能已经不存在（被删了）——补一个选项，否则下拉会显示成第一个，
  // 而**那是个谎**：会话实际绑的还是那个不存在的 id。
  if (currentId && !providers.some((p) => p.id === currentId)) {
    const o = document.createElement("option");
    o.value = currentId;
    o.textContent = currentId + "（已不存在）";
    o.selected = true;
    sel.appendChild(o);
  }
  sel.onchange = () => onPick(sel.value);
  return sel;
}

function modelSelect(providerId, currentModel, onPick) {
  const p = providers.find((x) => x.id === providerId);
  // 清单没声明 ⇒ 退回文本框（「没声明清单」不等于「没有模型」）。
  if (!p || !p.models || p.models.length === 0) {
    const input = document.createElement("input");
    input.value = currentModel || "";
    input.onchange = () => onPick(input.value.trim());
    return input;
  }
  const sel = document.createElement("select");
  const opts = [...p.models];
  // 当前模型不在清单里也要能选中——否则显示成第一个，而那是谎。
  if (currentModel && !opts.includes(currentModel)) opts.unshift(currentModel);
  for (const m of opts) {
    const o = document.createElement("option");
    o.value = m; o.textContent = m;
    if (m === currentModel) o.selected = true;
    sel.appendChild(o);
  }
  sel.onchange = () => onPick(sel.value);
  return sel;
}


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
}

// 合法协议 / 认证方式 / id 规则的**唯一**来源（核心）。⚠️ 头不许手抄——曾抄错一次协议默认值。
async function loadSchema() {
  try { SCHEMA = await (await fetch(core + "/provider-schema")).json(); } catch { /* 拿不到就用空表 */ }
}

// 发一条命令并**等它的回执**（`command` 是发了不管；表单要能根据成败决定下一步）。
// ⚠️ 超时兜底：核心不回的话不能永远挂着（有界 10 秒，与 provider 自检同一个量级）。
function commandNow(id, payload) {
  return new Promise((resolve) => {
    if (!wsReady) { resolve({ accepted: false, reason: "没连上核心" }); return; }
    const to = setTimeout(() => {
      if (pendingAcks.delete(id)) resolve({ accepted: false, reason: "核心没有回执（10 秒）" });
    }, 10000);
    pendingAcks.set(id, (frame) => { clearTimeout(to); resolve(frame); });
    ws.send(JSON.stringify({ kind: "command", id, payload }));
  });
}

// 供应商那份清单变了 ⇒ 会话视图的下拉要跟着变（否则用户在下拉里看不到刚建的那个）。
function refreshProviderChoices() {
  if (current) openSession(current);
}

// 会话视图右栏的「模型」块：两个可改的下拉（提供商 / 模型）+ 改绑的即时反馈。
// ⚠️ 提供商那块**不在这里编辑**——编辑在供应商视图（`05 §2.2`：「配置提供商 → 建初始会话」
// 是头的策略）。这里只回答问题「现在用的是哪个」并允许**改绑**。
function renderModelPicker(spec) {
  const box = $("model-picker");
  box.innerHTML = "";
  const note = $("model-note");
  note.textContent = "";

  const pid = spec.model?.providerId;
  const mid = spec.model?.modelId;
  const dl = document.createElement("dl");
  dl.className = "kv";

  const bind = (providerId, modelId) => {
    commandNow(crypto.randomUUID().slice(0, 8),
      // ⚠️ 键名是 `modelId`，**不是** `model`（2026-10-18 修）。
      // 核心曾经读 `"model"`，而 `03` §3 的签名与 `/state` 的 `spec.model.modelId` 都是 `modelId`
      // ⇒ 头与文档一致、核心自成一格。现在核心认 `modelId`，这里跟着改；
      // `provider.upsert` 的键仍叫 `model`（那是 `defaultModel`，不是同一个字段）。
      { kind: "session.configure", sessionId: current, providerId, modelId })
      .then((r) => { if (!r.accepted) { note.textContent = "✗ " + r.reason; note.className = "note-bad"; } });
    // ⚠️ 改完**不**本地改显示：等核心的 `spec.changed`（那是真相）。
    // 本地先改的话，核心若拒绝，页面会先显示成功再被打脸。
  };

  // 提供商下拉
  const dtP = document.createElement("dt"); dtP.textContent = "提供商";
  const ddP = document.createElement("dd");
  ddP.appendChild(providerSelect(pid, (v) => bind(v, null)));

  // 模型下拉（清单来自该提供商的 `models`；没声明就退回文本框）
  const dtM = document.createElement("dt"); dtM.textContent = "模型";
  const ddM = document.createElement("dd");
  ddM.appendChild(modelSelect(pid, mid, (v) => bind(null, v)));

  dl.append(dtP, ddP, dtM, ddM);
  box.appendChild(dl);

  if (!providers.length) {
    note.textContent = "还没有提供商。切到「供应商」建一个。";
    note.className = "note-bad";
  } else if (pid && !providers.some((p) => p.id === pid)) {
    // 会话绑的那个被删了 —— 这正是 `session.configure` 存在的理由，把话说清楚。
    note.textContent = `会话绑的供应商 '${pid}' 已不存在。在「供应商」里重建它，或从上面挑一个换过去。`;
    note.className = "note-bad";
  }
}

function renderSessionPanel(state) {
  const spec = state.spec || {};
  const dl = document.createElement("dl");
  dl.className = "kv";
  // ⚠️ 提供商 / 模型 / 协议 / 思考强度**不在这里**——它们住在上面那块「模型」里
  // （可改的下拉），在这里再列一遍就是同一事实的两个显示点，改了一个忘一个。
  // ⚠️ 名字**也不在这里**：它住 #title（中栏顶上）。同一个事实只该有一个显示点。
  kv(dl, [
    ["会话", spec.sessionId],
    ["创建", shortTime(spec.createdAt)],
    ["归档", spec.archivedAt ? shortTime(spec.archivedAt) : null],
  ]);
  $("sess-detail").innerHTML = "";
  $("sess-detail").appendChild(dl);

  renderSessionActions(spec);
  renderModelPicker(spec);
}

// 会话级的两个动作：归档 / 取消归档、删除。
// ⚠️ 它们与「改绑供应商」不是一类：改绑可逆，**删除不可逆**——
// 而删除抹掉的是 `history.jsonl`，全仓唯一一份不可从代码重现的东西。
function renderSessionActions(spec) {
  const box = $("sess-actions");
  box.innerHTML = "";
  const id = spec.sessionId;
  if (!id) return;

  const archived = !!spec.archivedAt;

  box.append(
    btn(archived ? "取消归档" : "归档", () => archiveSession(id, !archived)),
    btn("删除", () => deleteSession(id, spec), "btn-ghost"),
  );
}

async function archiveSession(id, archive) {
  // `archived` 为 null ⇒ 归档；false ⇒ 取消归档（核心侧那份三态）。
  const payload = { kind: "session.archive", sessionId: id };
  if (!archive) payload.archived = false;

  const r = await commandNow(crypto.randomUUID().slice(0, 8), payload);
  if (!r.accepted) { renderMeta("✗ " + r.reason, "error"); return; }
  // 事件也会来（`session.archived`），但那条只让左栏重画；右栏这几个字段要重拉快照。
  if (current === id) openSession(id);
}

// ⚠️ **`confirm()` 不是屏障**，别拿它当安全机制：核心那边靠自己「检查 + 删」一气呵成
// （两个核心共享数据根时，前端在离得远的地方做的检查挡不住互删）。
// 这里的「键入 id 才放行」是**防误点**——用户手上那个值必须是**真的 id**，
// 而不是一条随手回车就能过的确认。删掉的东西**回不来**。
async function deleteSession(id, spec) {
  const label = spec.title ? `${spec.title}（${id}）` : id;
  if (!confirm(`删除会话 ${label}？\n\n`
    + `这会删掉整个会话目录：对话记录、状态、配置。\n`
    + `⚠️ 记录只追加、从不另存，删了**回不来**。\n\n`
    + `它占的声明会一并释放（回执里会点名）。`)) return;

  const typed = prompt(`确认删除：请键入会话 id\n\n${id}`);
  if (typed === null) return;
  if (typed.trim() !== id) { renderMeta("✗ id 不匹配，没有删除。", "error"); return; }

  const r = await commandNow(crypto.randomUUID().slice(0, 8), { kind: "session.delete", sessionId: id });
  if (!r.accepted) { renderMeta("✗ " + r.reason, "error"); return; }
  // 会话已不在 ⇒ 事件会让左栏重画（`session.deleted`）；万一它正开着，也要清中栏。
  // ⚠️ **顺序不能反**（2026-10-06 修）：`clearCenter` 会清空时间线，先画回执那句话
  // 会被它一起抹掉。这是「回收」那处完全相同的形状——凡是「回执里要说一句话」+
  // 「这一步还要换掉中栏」的命令，都得先换、后说。
  if (current === id) clearCenter();
  if (r.note) renderMeta("ℹ " + r.note);
}

// 中栏回到「没选会话」。⚠️ **必须真的清空**：停在一条已经被删掉的时间线上是最糟的谎言
// （用户会以为自己看的是现存的东西）。
function clearCenter() {
  current = null;
  $("timeline").innerHTML = "";
  $("title").innerHTML = "";
  $("title").className = "muted";
  $("title").textContent = "未选会话";
  $("status").textContent = "未连接";
  $("status").className = "chip";
  $("interrupt").disabled = true;
  $("gc").disabled = true;
  $("sess-detail").innerHTML = '<div class="note">未选会话。</div>';
  $("proj-detail").innerHTML = '<div class="note">未选会话。</div>';
  $("model-picker").innerHTML = "";
  $("model-note").textContent = "";
  $("sess-actions").innerHTML = "";
  renderSessions();
}

// 中栏顶上那一行：**有名字就显示名字**（id 退到后面当等宽副标题），没有就还是 id。
// ⚠️ 名字不在这里改——今天**没有任何命令能改它**（所以也没有 `session.renamed` 事件），
// 只有 `session.create --title` 那一次。别做成可点的输入框，那会指向一个不存在的命令。
function renderTitle(spec) {
  const el = $("title");
  el.innerHTML = "";
  el.classList.remove("muted");

  if (spec && spec.title) {
    const name = document.createElement("span");
    name.className = "tname";
    name.textContent = spec.title;
    const id = document.createElement("span");
    id.className = "tid";
    id.textContent = spec.sessionId;
    el.append(name, id);
  } else {
    el.textContent = spec ? spec.sessionId : "";
  }
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

  // ⚠️ **「我手上是哪一版」必须能查，否则每次「改了没效果」都要靠猜。**
  // `app.js` 从来不换名字，而同一份改动**有三份副本**（`head/wwwroot/` 是源，
  // `bin/Debug` 与 `bin/Release` 各复制一份）——跑哪个构建就看哪一份。
  // ⇒ 这一格显示**提供本页面的那份** `app.js` 的大小与时间戳，与源码一比对答案立刻出来。
  try {
    const b = await (await fetch("/build.json")).json();
    const s = document.createElement("span");
    s.textContent = ` · 页面 ${b.appJsBytes}B @${(b.appJsWritten || "?").slice(11)}`;
    s.title = b.servedFrom ?? "";
    $("bar-version").append(s);
  } catch {
    // 老的核心/头没有这个端点 ⇒ 什么都不加（别让底栏出现一句看不懂的错）。
  }
}

// 断开时去问一句「为什么」，而不是永远「已断开 · 3 秒后重连」。
//
// ⚠️ **必须自己去问**：WS 握手被核心拒绝时（Origin 白名单，见核心的 `MapWebSocket`），
// 浏览器**不把 403 响应体交给页面 JS**——页面只看到 onclose，因此永远拿不到原因。
// 核心已经把「它接受哪个 origin」交在 `/info` 里了，这里只是把它读出来。
//
// ⚠️ **只说证据支持得住的话**：/info 拿不到 ⇒ 多半是核心没起来，那就不改文案——
// 此时说「origin 被拒」是编的。拿得到、且与 `location.origin` 对不上，断开才几乎一定是这个原因。
//（`location.origin` 正是浏览器放进 `Origin` 头的那个值，不必再推导一遍。）
async function diagnoseOrigin() {
  let i;
  try { i = await (await fetch(core + "/info")).json(); } catch { return; }
  if (wsReady) return;   // 这趟 fetch 期间重连成功了，别拿过期的诊断盖掉「已连接」
  if (!i.allowedOrigin || i.allowedOrigin === location.origin) return;
  $("status").textContent = `连不上：核心只接受来自 ${i.allowedOrigin} 的页面（当前 ${location.origin}）`;
  $("status").className = "chip down";
}

// ---- 轴（义务 4 的落点） ----
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

// ⚠️ **别再给这里加「缓存节点引用」的兜底**（曾有一个 `caret` 对象，已删）。
// 根因是**换进去的新控件丢了 id**：`$("ax-safety")` 从此永远是 null，
// 而 `renderAxes` 开头那一句是**直接查 DOM** 的 ⇒ 第二次渲染必炸
// （实测：`Cannot set properties of null (setting 'innerHTML')` at `renderAxes`）。
// ⇒ 修法是**让新节点带上同一个 id**，`$()` 就一直拿得到——**一个机制，不是两个**。

function renderAxes(values) {
  axes = values || {};
  // ⚠️ **先拿、再用**：这个节点每次渲染都会被换掉（见下面 `replaceWith`），
  // 所以不能分开查两次，也不能假设它还是 HTML 里那一个。
  const secSel = $("ax-safety");
  secSel.innerHTML = "";
  $("axes").innerHTML = "";

  for (const a of AXES) {
    const row = document.createElement("div");
    // inContext 由核心给（effort / 温度不进上下文），头不手抄。
    row.className = "axis" + (a.inContext ? "" : " no-context");

    // ⚠️ 数值范围轴（核心交 `range`）画**数字输入**，不画下拉——21 个选项的下拉没人用。
    // 判据是 `a.range` 的有无，不是我这边认轴名。
    // ⚠️ 旧核心不交 range ⇒ 这条路走 select 分支，照样画得出来（核心先上线、头后跟）。
    let ctl;
    if (a.range) {
      ctl = document.createElement("input");
      ctl.type = "number";
      ctl.min = a.range.min; ctl.max = a.range.max; ctl.step = a.range.step;
      // 值可能规范化过（`1.0`）而 `type=number` 只认 `1`——交给浏览器不必较真，它认得出。
      ctl.value = axes[a.name] ?? "";
    } else {
      ctl = document.createElement("select");
      for (const v of a.values) {
        const o = document.createElement("option");
        o.value = v; o.textContent = v;
        if (axes[a.name] === v) o.selected = true;
        ctl.appendChild(o);
      }
    }

    ctl.disabled = a.available === false;
    if (a.problem) ctl.title = a.problem;
    if (!a.range && axes[a.name] != null && !a.values.includes(axes[a.name])) {
      const saved = document.createElement("option");
      saved.value = axes[a.name]; saved.textContent = axes[a.name]; saved.selected = true;
      ctl.appendChild(saved);
    }
    const axisName = a.name;   // 闭包捕获，别用循环变量
    const send = () => {
      if (!current) return;
      const value = ctl.value;
      // 空值不发（用户把数字框清空的那一刻不该发一条空轴值）。
      if (value === "") return;
      commandNow(crypto.randomUUID().slice(0, 8),
        { kind: "session.setAxis", sessionId: current, axis: axisName, value })
        .then((r) => {
          // ⚠️ 被拒时**必须把控件退回真相**：数字框还留着用户敲的 0.75，而核心没接受它。
          // 不退的话页面上那个值看起来是设上了的——那是谎。
          if (!r.accepted) { ctl.value = axes[axisName] ?? ""; renderMeta("✗ " + r.reason, "error"); }
        });
    };
    ctl.onchange = send;

    const lab = document.createElement("label");
    lab.textContent = a.label;

    // 安全轴在右栏最上面单独一份，这里跳过它免得出现两个控件；标签也要填（从核心来）。
    // ⚠️ 判据是「这条轴的取值里含着告警线那一端」——**不是**比 "safety" 字面量：
    //    轴名归核心，头这里只能按核心给的数据认。
    if (a.values && a.values.includes(ALERT_SAFETY)) {
      $("ax-safety-label").textContent = a.label;
      // ⚠️ **换进去的新控件必须带上同一个 id**：不带的话 `$("ax-safety")` 从此是 null
      // （`replaceWith` 搬走的是**原来那个**节点），而本函数开头正是靠 `$()` 拿它的。
      // 这就是「第二次打开会话时时间线空白」那个 bug——`renderAxes` 在 `openSession` 里
      // 抛异常，而它在**清空时间线之后、渲染历史之前** ⇒ 历史永远渲染不出来。
      ctl.id = "ax-safety";
      secSel.replaceWith(ctl);
      continue;
    }

    row.appendChild(lab); row.appendChild(ctl);
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
  staleClaims.clear();
  for (const c of all) {
    if (!claims.has(c.sessionId)) claims.set(c.sessionId, new Set());
    claims.get(c.sessionId).add(c.path);
    if (c.stale) staleClaims.add(c.path);
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

      // ⚠️ **残声明**：它指向一个已经不存在的会话（`session.delete` 是两步——先摘声明、
      // 再删目录，崩在中间就会留下这种；手删目录也会）。核心**不自动清理**它
      // （在别人正活跃地写 claims.json 时从别处删条目会打架），但必须**看得见**——
      // 否则它会静静地骗人：「这个文件有人占着」，而那个「人」早就没了。
      if (staleClaims.has(p)) {
        const tag = document.createElement("span");
        tag.className = "stale";
        tag.textContent = "会话已不存在";
        tag.title = "这条声明的主人已经不在盘上了。核心不会自动清理它（见 /claims 的 stale 字段）。";
        el.appendChild(tag);
      }
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
    diagnoseOrigin();          // 不给原因就是这个形状的全部问题（见函数注释）
    setTimeout(connect, 3000);
  };

  ws.onmessage = (e) => {
    const frame = JSON.parse(e.data);

    if (frame.kind === "ack") {
      // ⚠️ **等回执的那条路最先判、且判完就 return**：表单发的命令（provider.upsert /
      // session.configure）由 `commandNow` 的 promise 消费它的成败。不 return 的话，
      // 同一条回执还会往时间线上再打一行——用户看到两次「✗」，一次在表单里、一次在会话里。
      if (frame.id && pendingAcks.has(frame.id)) {
        const resolve = pendingAcks.get(frame.id);
        pendingAcks.delete(frame.id);
        resolve(frame);
        return;
      }

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
        // 只在看着供应商视图时重画（在会话视图里刷那边没意义；切过去时自然会画上）。
        if (document.body.classList.contains("providers")) renderProvidersView();
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

    // ⚠️ 这三条**不能只靠 `session.created` 那种「重拉列表」**：
    // 归档态住在每个会话的 `spec` 里，而 `loadSessions` 的循环会**跳过已知会话**
    // （`if (info.has(id) && ...spec) continue`），所以重拉列表**读不出归档态的变化**。
    // ⇒ 必须逐条把那个会话的 `/state` 重拉一遍。
    case "session.archived":
      refreshSessionInfo(p.sessionId); break;

    case "session.deleted":
      // 会话没了：把它从所有本地缓存里摘掉，再重拉列表。
      info.delete(p.sessionId);
      sessions.delete(p.sessionId);
      claims.delete(p.sessionId);
      // ⚠️ 删掉的**正是当前打开的那个** ⇒ 中栏必须清空。停在一条已经被删掉的时间线上
      // 会让用户以为看的是现存的东西——那是本页能犯的最大的谎。
      if (current === p.sessionId) clearCenter();
      loadSessions();
      renderClaims();
      break;

    // ⚠️ `session.deactivated` **刻意什么都不做**：头的界面完全不显示「激活」这件事
    //（快照端点会自己回落磁盘）。这条事件是给**别的客户端**（控制台）看的——
    // 别因为这里不用就把它从核心删掉。
    case "session.deactivated": break;
  }
}

// 重拉单个会话的快照缓存（列表里的名字 / 步数 / 状态 / 归档态都从它来）。
async function refreshSessionInfo(id) {
  try {
    const st = await (await fetch(`${core}/sessions/${id}/state`)).json();
    info.set(id, { spec: st.spec, turn: st.state.turn, status: st.state.status });
  } catch {
    // 读不出来（刚被删？）⇒ 摘掉缓存，别留一条陈旧的行。
    info.delete(id);
  }
  renderSessions();
  if (id === current) openSession(id);
}

function handleEvent(p) {
  switch (p.kind) {
    case "message.appended":
      if (p.sessionId === current)
        renderTurn(p.from, p.blocks, null, p.historyId);
      break;

    // ⚠️ **工具结果的实时出口**（2026-10-17）。此前核心只在步末发模型消息，
    // 结果一条事件都没有 ⇒ 头要把页面重拉一遍才看得见（用户实测）。
    // ⇒ 与 `message.appended` 对称：带 `historyId`，`appendToolResult` 负责按 `callId`
    // 配到前面那条调用下面；重拉时同一批结果会以相同 id 出现，去重由 id 完成。
    case "tool.result.appended":
      if (p.sessionId === current)
        appendToolResult({ kind: "toolResult", id: p.id, callId: p.callId, blocks: p.blocks });
      break;

    case "session.step.begin":
      // 「在跑」的信号源是它：session.state 是磁盘的镜像，而磁盘从不存 Running。
      if (p.sessionId === current) setStatus("Running", p.turn - 1);
      break;

    // ⚠️ **重试上报**（2026-10-21，`retry.attempt`）。没有它的话，一次十次退避的失败在头这边
    // 就是 `Running` **纹丝不动**四十秒——用户看不出「在重试」与「卡死了」的区别。
    // ⚠️ **不画进时间线**（`renderTurn` 那种）：重试不是对话里的一条，它是**这一步的临时状态**，
    // 所以落在状态条上，`step.end` / `state` / `error` 一到就自然消失。
    // ⚠️ `p.attempt` 是**刚刚失败的那一次**，而「重试 N/10」说的是**即将发出的那次**的序号
    // ——两者恰好相等（失败第 1 次 ⇒ 接下来是第 2 次尝试 = 第 1 次重试）；核心的载荷就是按这个意思给的。
    case "retry.attempt":
      if (p.sessionId === current)
        setStatus("Running", null,
          `重试 ${p.attempt}/${p.maxAttempts} · ${(p.delayMs / 1000).toFixed(1)} 秒后再试`);
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

    case "spec.changed":
      // 改绑（换供应商 / 换模型）的回声。⚠️ **不在这里改本地缓存再重画**——
      // 直接拉一次快照（`openSession`），因为模型清单、provider 是否存在这些
      // 都要跟着重取。一条命令一次快照，量很小。
      if (p.sessionId === current) openSession(current);
      break;

    case "session.reclaimed":
      // ⚠️ **回收不改盘**：核心只是换了一份渲染，而 `/history` 拿的就是那份
      // ⇒ 不重拉的话，页面会一直显示回收**之前**的形态，而且完全看不出来。
      // 判据是「该重拉了」，删什么、怎么显示由核心说了算（判定只有 `Reclaim.Decide` 一处）。
      //
      // ⚠️ **成果那句话画在重拉之后**（2026-10-06 修）：回执**先到**（它是同步返回的），
      // 而这条事件后到。原先只靠回执画那行 ⇒ 这条事件触发重拉时会把刚画的那行**抹掉**
      // ⇒ 用户看到的就是「按了没反应」。现在话随事件来、画在重拉之后，位置就不再重要。
      // ⚠️ 判据是 `p.sessionId === current`：换了会话之后这个回声不该落在新会话上。
      if (p.sessionId === current) {
        openSession(current).then(() => { if (p.reason) renderMeta("ℹ " + p.reason); });
      }
      break;

    case "release.recorded":
      // ⚠️ **模型画了一条线**（`release`）：线落在 `history.jsonl` 里，而 `/history` 现在
      // 会把它交出来 ⇒ 重拉一次就能在正确位置画出分隔线。⚠️ **不在这里手动插一行**——
      // 线的位置由核心的分页给出，手插会让它和重拉后的顺序打架（同 `session.reclaimed`）。
      if (p.sessionId === current) openSession(current);
      break;

    // ⚠️ **询问**（`01` §3.8）。它同时投 `approvals` 与会话流 ⇒ 这里收到的是**一条**
    // （两次投递落在**两个不同的 topic** 上，而 `subscribe` 是每 topic 一条连接）。
    //
    // ⚠️ 回答的 UI **已经有了**（见 `renderAsk` / `askButtons`）：多问 + 混合类型 +
    // 改写参数 + remember 两档。这里只负责把列表上那颗徽标点亮、并重拉一次。
    case "approval.requested":
      {
        // ⚠️ **`askKind`**（2026-10-11 改）——字段名曾经是 `askKindName`，因为那时它是
        // 一个 `string`（手写 `ToString()`）。现在核心直接交枚举、由序列化器出值，
        // 于是字段名回到了 `askKind`，**值是小写 `"permission"` / `"question"`**
        // （与 `/sessions` 的 `ask.kind`、`/state` 的 `ask.kind` 三处同一个来源）。
        const v = info.get(p.sessionId);
        if (v) { v.ask = { askId: p.askId, kind: p.askKind, deadlineAt: p.deadlineAt }; renderSessions(); }
        if (p.sessionId === current) {
          setStatus("WaitingForInput");
          // ⚠️ **重拉**，不是画一行字：那张卡片要的问题原文**不在事件里**
          // （事件带的是内存里那份 `items`，但核心侧 `/state` 才是头的真相来源，
          // 而重启之后两者会不一致）。重拉一次两条路就归到同一个答案上。
          openSession(current);
        }
      }
      break;

    case "approval.resolved":
      {
        const v = info.get(p.sessionId);
        if (v) { v.ask = null; renderSessions(); }
        if (p.sessionId === current) {
          renderMeta("（那次询问已结束：" + p.outcome + "）");
          // ⚠️ 卡片要消失——它由 `/state` 画，所以重拉一次，而不是就地删节点
          // （就地删会让面板与核心状态分叉，而下一次事件回来又画出来）。
          openSession(current);
        }
      }
      break;

    case "usage.updated":
      // 账本事件已经带回本次调用的核心事实；会话页即时显示，统计页重新读取
      // 全局投影，避免把「当前会话汇总」误当成全局汇总。
      if (p.sessionId === current && p.call) {
        renderContextStatus({
          latest: {
            callId: p.call.callId,
            startedAt: p.call.startedAt,
            endedAt: p.call.endedAt,
            modelId: p.call.modelId,
            protocol: p.call.protocol,
            inputTokens: p.call.usage?.inputTokens,
            source: p.call.usage ? "reported" : "unknown",
            requestFingerprint: p.call.requestFingerprint,
          },
        });
      }
      if (document.body.classList.contains("usage")) loadUsageView();
      break;

    case "session.compaction.completed":
      if (p.sessionId === current) {
        openSession(current).then(() => {
          if (p.coveredThroughEventId) renderMeta("ℹ 上下文已压缩至 " + p.coveredThroughEventId);
        });
      }
      if (document.body.classList.contains("usage")) loadUsageView();
      break;

    case "error":      // 业务失败走这里（不是 rejected：命令本身没写错）。
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
$("nav-sessions").onclick = () => showView("sessions");
$("nav-providers").onclick = () => showView("providers");
$("nav-usage").onclick = () => showView("usage");
$("usage-refresh").onclick = () => loadUsageView(true);
$("usage-prev").onclick = () => { usageSkip = Math.max(0, usageSkip - usagePageSize); loadUsageView(); };
$("usage-next").onclick = () => { usageSkip += usagePageSize; loadUsageView(); };

// ---- 新建会话：目录输入即过滤 + 起名 ----
//
// ⚠️ 分工（用户裁定 2026-10-17）：**补全逻辑在头**，core 侧只需要一个 ls——
// `GET /directories?path=` 给什么列什么（空路径列盘符/根，路径列直接子目录），
// **不认前缀、不做匹配**。头把输入切成「父目录 + 名字前缀」，问 core 要父目录的
// 子目录，再在本地按前缀过滤并画成候选。
//
// ⚠️ 两条容易写错的地方（都是「静态看图看不出来」）：
//   ① **请求次序**：先发的慢响应不能盖掉后发的快响应（`dirBrowseSeq`）；
//      否则打字打快一点，候选就停在某一个中间状态，而且不报错。
//   ② **父目录的枚举**：`E:/Worksp` 的父目录是 `E:/`，**不是** `E:/` 本身——
//      「列出 `E:/Worksp` 的子目录」会被 core 判成「目录不存在」而报错，而用户
//      想要的是「`E:/` 下以 `Worksp` 开头的那些」。
let dirBrowse = { seq: 0, items: [], active: -1, prefix: "" };

$("new").onclick = async () => {
  $("new-form").hidden = false;
  $("new-note").textContent = "";
  $("new-note").className = "note";
  $("new-title").value = "";
  $("new-project").value = "";
  await dirSuggest("");
  $("new-project").focus();
};

$("new-cancel").onclick = () => {
  $("new-form").hidden = true;
  hideDirList();
};

// 输入 → 请求（防抖 120ms：连续打字只发最后一次）；打开表单时主动拉一次盘符。
let dirTimer = null;
$("new-project").addEventListener("input", () => {
  clearTimeout(dirTimer);
  dirTimer = setTimeout(() => dirSuggest($("new-project").value), 120);
});

// 把 `E:/Worksp` 拆成「父目录 E:/」＋「前缀 Worksp」。
//
// ⚠️ 分隔符必须**两种都认**：core 在 Windows 上回的是 `\`，而用户在框里打的是 `/`。
// 只认一种的话 `E:\Workspace\` 会被当成一个名字，前缀过滤永远匹配不上。
function dirSplit(input) {
  const text = String(input ?? "");
  const cut = Math.max(text.lastIndexOf("/"), text.lastIndexOf("\\"));
  if (cut < 0) return { dir: "", prefix: text };
  // `E:/` 与 `/` 的 cut 落在最后一位：父目录就是它自己，前缀为空。
  if (cut === text.length - 1) return { dir: text, prefix: "" };
  return { dir: text.slice(0, cut + 1), prefix: text.slice(cut + 1) };
}

async function dirSuggest(input) {
  const { dir, prefix } = dirSplit(input);
  const seq = ++dirBrowse.seq;
  dirBrowse.prefix = prefix;

  try {
    const query = dir ? `?path=${encodeURIComponent(dir)}` : "";
    const r = await fetch(`${core}/directories${query}`);
    const data = await r.json();
    if (seq !== dirBrowse.seq) return;      // 过期的响应：丢掉，不画

    if (!r.ok) {
      dirBrowse.items = [];
      renderDirItems([], data.error || "目录不可用", prefix);
      return;
    }

    const p = prefix.toLowerCase();
    const items = (data.directories || []).filter((d) => d.name.toLowerCase().startsWith(p));
    // 输入的父目录本身就是待选路径时（如 `E:/Workspace/`），第一项放它自己，
    // 否则「进入子目录后想停在这里」只能靠手删——那是这一版要消掉的来回。
    const here = dir && prefix === "" ? [{ name: "此目录", path: dir, up: false, here: true }] : [];
    dirBrowse.items = here.concat(items);
    renderDirItems(dirBrowse.items, data.error, prefix);
  } catch (e) {
    if (seq !== dirBrowse.seq) return;
    dirBrowse.items = [];
    renderDirItems([], "读不到 core 设备目录：" + (e && e.message ? e.message : e), prefix);
  }
}

function renderDirItems(items, error, prefix) {
  const list = $("new-project-list");
  list.innerHTML = "";
  dirBrowse.active = -1;

  if (error) {
    const err = document.createElement("div");
    err.className = "dir-error";
    err.textContent = "⚠️ " + error;
    list.appendChild(err);
    showDirList();
    return;
  }

  if (items.length === 0) {
    const empty = document.createElement("div");
    empty.className = "dir-empty";
    empty.textContent = prefix ? `没有以「${prefix}」开头的子目录` : "没有子目录";
    list.appendChild(empty);
    showDirList();
    return;
  }

  items.forEach((d, i) => {
    const row = document.createElement("div");
    row.className = "dir-item";
    row.setAttribute("role", "option");
    // ⚠️ 用 dataset 存路径，不把路径拼进 onclick——`innerHTML` 拼路径会让 `'` 和
    // 模型/用户能碰到的一切文本变成代码；目录名里出现引号不是假设，是输入。
    row.dataset.path = d.path;
    row.dataset.index = String(i);

    const name = document.createElement("span");
    name.className = "name" + (d.here ? " up" : "");
    name.textContent = d.here ? "↳ 选择当前目录" : d.name + (prefix && d.name.length === prefix.length ? "/" : "");
    name.title = d.path;

    const full = document.createElement("span");
    full.className = "full";
    full.textContent = d.path;
    full.title = d.path;

    row.append(name, full);
    row.addEventListener("mousedown", (e) => {
      // mousedown 而不是 click：click 之前 input 会先 blur，列表已经被隐藏。
      e.preventDefault();
      pickDir(d);
    });
    list.appendChild(row);
  });

  showDirList();
}

function showDirList() {
  const list = $("new-project-list");
  list.hidden = false;
  $("new-project").setAttribute("aria-expanded", "true");
}

function hideDirList() {
  const list = $("new-project-list");
  if (!list) return;
  list.hidden = true;
  list.innerHTML = "";
  dirBrowse.active = -1;
  $("new-project").setAttribute("aria-expanded", "false");
}

// 选中一个候选：
//  - 「当前目录」项直接落成值；
//  - 子目录**不直接落成值**，而是补全到「子目录/」并继续列下一层——这样
//    `E:/` → `E:/Worksp` → `E:/Workspace/` 一路是「打字 + 回车」，不用再点「进入」。
function pickDir(d) {
  const input = $("new-project");
  if (d.here) { input.value = d.path; hideDirList(); return; }
  input.value = d.path.endsWith("/") || d.path.endsWith("\\") ? d.path : d.path + (d.path.includes("\\") ? "\\" : "/");
  dirSuggest(input.value);
  input.focus();
}

function moveDirActive(step) {
  const rows = [...$("new-project-list").querySelectorAll(".dir-item")];
  if (rows.length === 0) return;
  dirBrowse.active = (dirBrowse.active + step + rows.length) % rows.length;
  rows.forEach((r, i) => r.classList.toggle("on", i === dirBrowse.active));
  rows[dirBrowse.active].scrollIntoView({ block: "nearest" });
}

$("new-project").addEventListener("keydown", (e) => {
  const open = !$("new-project-list").hidden;
  if (e.key === "ArrowDown") { e.preventDefault(); open ? moveDirActive(1) : dirSuggest($("new-project").value); }
  else if (e.key === "ArrowUp") { e.preventDefault(); if (open) moveDirActive(-1); }
  else if (e.key === "Enter") {
    // 有高亮就选它；没有高亮且候选恰好一条就选那一条（常见路径不用按 ↓）。
    const row = open && (dirBrowse.active >= 0 ? dirBrowse.items[dirBrowse.active]
      : dirBrowse.items.length === 1 ? dirBrowse.items[0] : null);
    if (row) { e.preventDefault(); pickDir(row); }
  } else if (e.key === "Escape") { e.preventDefault(); hideDirList(); }
});

$("new-project").addEventListener("blur", () => hideDirList());

$("new-go").onclick = async () => {
  const note = $("new-note");
  const id = crypto.randomUUID().slice(0, 8);
  const cwd = $("new-project").value.trim();
  const title = $("new-title").value.trim();

  if (!cwd) { note.textContent = "✗ 先从 core 设备选择一个目录。"; note.className = "note-bad"; return; }
  hideDirList();

  const payload = { kind: "session.create", cwd };
  if (title) payload.title = title;

  const r = await commandNow(id, payload);
  if (!r.accepted) {
    // 名字没打完（`E:/Worksp`）就点创建是最常见的一步：把 core 的拒绝换成可操作的提示，
    // 并顺手补全到「候选唯一」的那一层——别的失败原样照转。
    if (r.reason && r.reason.startsWith("目录不存在")) {
      note.textContent = "✗ " + r.reason + "（输入框里的路径可能还没补全：先用 ↓ 选候选或补上结尾的 /）";
      dirSuggest(cwd);
    } else {
      note.textContent = "✗ " + r.reason;
    }
    note.className = "note-bad";
    return;
  }
  $("new-form").hidden = true;

  try {
    const st = await (await fetch(`${core}/sessions/${r.sessionId}/state`)).json();
    currentProject = st.spec.projectKey;
    await loadSessions();
    await openSession(r.sessionId);
  } catch (e) {
    renderMeta("✗ 会话建好了但打不开：" + (e && e.message ? e.message : e), "error");
  }
};

$("interrupt").onclick = () => {
  if (current) command(crypto.randomUUID().slice(0, 8), { kind: "session.interrupt", sessionId: current });
};

// 回收（`01` §8.0.2，手动触发）。
//
// ⚠️ **走 `commandNow`，用回执里带回来的 `sessionId` 去重拉 —— 不用 `current`**：
// `session.gc` 是**同步**算完才回执的，而在这整个窗口里 `current` 还是**别的会话**。
// 用 `current` 的话有两处错，而且都不报错：
//   ① 用户在回执回来之前切了会话 ⇒ 重拉的是**新会话**，而**刚被回收的那个永远不刷新**；
//   ② 连着点两下（或切完会话立刻点）⇒ 第二条命令带着**上一个会话的 id** 发出去。
// 回执里的那条 sessionId 是**我们发出去的那一条**，不受切换影响。
//
// ⚠️ 头**不自己算**「哪些能回收」：判定只有核心一处（`Reclaim.Decide`），
// 这里自己算一遍就是第二份判据——两边迟早会漂。
// ⚠️ **成果那句话不在这里画**（2026-10-06 修）：它随 `session.reclaimed` 来、画在**重拉之后**
// （见 `handleEvent` 那一段）。这里再画一遍的话有两种错：位置不对（紧接着就被重拉抹掉），
// 以及万一事件先到，用户会看到**两遍**同一句话。
//
// ⚠️ **这里只负责「被拒」**：`rejected` 那条路**不发事件**（命令本身没写错，没什么可重拉的），
// 所以那句话没有别的去处，只能在这儿说。
//
// ⚠️ 头**不自己算**「哪些能回收」：判定只有核心一处（`Reclaim.Decide`），
// 这里自己算一遍就是第二份判据——两边迟早会漂。
$("gc").onclick = () => {
  if (!current) return;
  commandNow(crypto.randomUUID().slice(0, 8), { kind: "session.gc", sessionId: current })
    .then((r) => { if (!r.accepted) renderMeta("✗ " + r.reason, "error"); });
};

// GET /providers 是引导要用的：端点不存在时 bootstrap() 会在 fetch().json() 上抛，
// 而那是最先跑的异步动作 ⇒ 整个页面停在空白。

(async () => {
  try { core = (await (await fetch("/config.json")).json()).core; } catch { }   // 取不到就用默认值
  // 视图先定（片段里写着就直接进那一页，省得闪一下会话视图再跳）。
  if (location.hash === "#providers") showView("providers");
  else if (location.hash === "#usage") showView("usage");
  connect();
  // 失败必须可见：不接的话是一次未观测拒绝（页面停在空白）；接住并打一行到时间线，
  // 核心起来后由 onopen 的 currentProject === null 分支自动重试。
  try {
    await bootstrap();
  } catch (e) {
    renderMeta("✗ 核心没连上（" + (e && e.message ? e.message : e) + "）——连上后会自动重试", "error");
  }
})();

// ---- 待审批（`01` §3.8） ----
// ---- 待决策：回答一次询问（`01` §3.8） -----------------------------------
//
// ⚠️ **判据一律是 `ask?.askId`**（不是 `ask`）：核心在「没在等」时交的是 `{}`，
// 而 `{}` 在 JS 里是 **truthy**（见 `ControlServer` 里那段注释）。
//
// ⚠️ **这一块的存在理由是「能回答」**，而不是「看得见」——看得见只是它的前提。
// 核心侧 `approval.respond` 是挂起会话的**唯一**解除入口，而它**在头上够不着**
// 就等于没有：用户不会为了点一个「不准」去开控制台。
//
// ⚠️ **问题原文从哪来**：`GET /sessions/{id}/state` 的 `ask`。
// ⚠️ **不是** `session.state` / `approval.requested` 那两条事件——`PausedAsk`（盘上那份）
// 只记「等哪一次调用、等多久」，而问题原文是**渲染产物**。⇒ 端点才是头的真相来源。
//
// ⚠️ **核心侧问题原文是从落盘的那条 `tool_use` 解出来的**（不是内存）——
// 所以**重启之后照样有**（实测过：重启后 `/state` 的 `items` 仍带 `shape` 与选项）。
// 那条「items 为空 ⇒ 显示原问题没能保留」的分支因此是**兜底**，不是常态。

// 待决策那一格。⚠️ `state.ask` 为 null = 没在等；有值而 `items` 空 = 问题没留住（见上）。
//
// ⚠️ **判据是 `state.ask` 存在，而<b>不是</b> `items.length > 0`**：
// 后者会把「问题没留住」那一格**误判成「没在等」** ⇒ 面板静默消失、而会话**卡死**
// （用户对着一个「没有在等回答」的提示，无从把它解开）。
function renderAsk(state) {
  const box = $("ask");
  if (!box) return;
  box.innerHTML = "";

  const ask = state && state.ask;
  if (!ask) {
    box.innerHTML = '<div class="note">这个会话没有在等回答。</div>';
    return;
  }

  // 问题原文。⚠️ 正常路径上**解得出**（核心从落盘那条 `tool_use` 解），
  // 空只可能是「模型的参数形状坏了」或「闸门那条询问（它没有工具参数）」。
  const items = Array.isArray(ask.items) ? ask.items : [];

  const note = document.createElement("div");
  note.className = "note";
  note.textContent = ask.kind === "permission"
    ? "模型想跑「" + ask.toolName + "」，等你点头。"
    : "模型在问你。";
  box.appendChild(note);

  if (ask.deadlineAt) {
    const dl = document.createElement("div");
    dl.className = "note warn";
    // ⚠️ 说清「过期就按没人回答处理」——用户据此知道不能先放着不管。
    dl.textContent = "⏱ " + shortTime(ask.deadlineAt) + " 之前有效，过期就按没人回答处理。";
    box.appendChild(dl);
  }

  // ---- 问题没留住那一格：没有原文，只能解掉 ----
  //
  // ⚠️ **措辞不写「重启过」**：那曾经是唯一的原因，而现在**不是**了——
  // 核心改成**从落盘的那条 `tool_use` 解问题**之后，重启也会解出来。
  // ⇒ 走到这里的只剩「模型的参数形状坏了」与「闸门那条询问（它没有工具参数）」。
  // ⚠️ 乱归因比不归因更坏（用户会去重启、去查数据根，而真相是别的东西）。
  if (items.length === 0) {
    const lost = document.createElement("div");
    lost.className = "banner noted";
    // ⚠️ **如实说**：状态是诚实的（核心知道自己还在等），而问题没留住。
    // 编一个问题问用户比这坏得多——他会以为模型真问了那个。
    lost.textContent = "这次询问的问题取不回来了（多半是模型给的参数形状不对）。"
      + "点下面那个按钮把它解掉，让模型重新问一次。";
    box.appendChild(lost);
    box.appendChild(askButtons(
      [{ label: "让它重新问", outcome: "cancel", why: "核心会把这次询问作废，模型重新发起" }],
      ask, new Map()));
    return;
  }

  // ---- 正常那格 ----
  const inputs = new Map();      // 问题 id -> () => 值

  for (const it of items) {
    const row = document.createElement("div");
    row.className = "askitem";

    const q = document.createElement("div");
    q.className = "askq";
    q.textContent = it.question || it.id;
    row.appendChild(q);

    inputs.set(it.id, askInputFor(it, row));
    box.appendChild(row);
  }

  // 记忆那个复选框。⚠️ **只在核心说可以**（`remember` 非空）时才有——
  // 问意见（`kind === "question"`）没有「记住」这回事（答案是内容，不是许可）。
  const remembers = Array.isArray(ask.remember) ? ask.remember : [];
  if (remembers.length > 0) {
    const rm = document.createElement("label");
    rm.className = "askremember";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    rm.appendChild(cb);

    const txt = document.createElement("span");
    // ⚠️ **粗度必须写出来**：`tool` 那档放行的是这个工具在**本会话里的任何参数**。
    // 与「记住这些参数」并排成两个长得差不多的复选框，就是让用户批了他没打算批的东西。
    txt.textContent = "本会话内不再问这个工具（任何参数都放行）";
    rm.appendChild(txt);
    rm.title = "勾上之后，同一个工具换参数也不再问你——它比「只批这一次」粗得多";
    box.appendChild(rm);

    inputs.set("__rememberTool", () => (cb.checked ? "tool" : null));
  }

  box.appendChild(askButtons([
    // ⚠️ **`decision` 是必须的**（2026-10-11 修）。核心判「批没批」的唯一依据是
    // `answers.decision === "allow"`，而这里原先**一颗按钮都不发它** ⇒
    // 点「批准」落成的是「用户拒绝了这次调用」（`Reply` 里那条 `!approved` 分支），
    // 而点「拒绝」多塞一个 `reason` ⇒ 两颗按钮发出去的东西**除了理由之外一模一样**。
    // 控制台一直是对的（`approval allow` 塞 `decision:"allow"`）——漂的只有头这一侧，
    // 而它**不报错**，只是每次都把用户的批准记成拒绝。
    // ⇒ 判据（`01` §3.8）：两层判据必须共用一个谓词。
    { label: "批准", outcome: "submit", decision: "allow", why: "批准这次调用（下一步它就会执行）" },
    { label: "拒绝", outcome: "submit", decision: "deny", deny: true, why: "拒绝，并把理由告诉模型" },
    { label: "作废", outcome: "cancel", why: "这次询问整个作废，模型重新发起" },
  ], ask, inputs));
}

// 按形状造一个输入控件，返回**取值函数**。
//
// ⚠️ 三个形状（`01` §3.8）与四个控件：`choice` 单选 / `choice+multi` 多选 /
// `text` 文本框 / `verbatim` 原样一段可改的参数 JSON / `form` 若干命名框。
// **`verbatim` 是核心造的**（参数名 `arguments` 是核心与协议之间的约定）⇒ 这里
// 不能按 `form` 渲染——它的值是**要原样回传的原文**，改一个字节就是改了参数。
function askInputFor(item, row) {
  const shape = item.shape;

  if (shape === "choice") {
    const opts = Array.isArray(item.options) ? item.options : [];
    if (item.multi) {
      const chosen = new Set();
      for (const o of opts) {
        const l = document.createElement("label");
        l.className = "askopt";
        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.onchange = () => { if (cb.checked) chosen.add(o.value); else chosen.delete(o.value); };
        l.append(cb, document.createTextNode(o.label || o.value));
        row.appendChild(l);
      }
      return () => [...chosen].join("、");
    }

    const sel = document.createElement("select");
    // ⚠️ **默认不预选**：`<select>` 的第一项会被浏览器自动选中，而那会让
    // 「用户没看就点了确认」变成一个**合法**的答案。插一个空占位项挡住它。
    const ph = document.createElement("option");
    ph.value = "";
    ph.textContent = "（请选一个）";
    sel.appendChild(ph);

    for (const o of opts) {
      const op = document.createElement("option");
      op.value = o.value;
      // ⚠️ label 空 = 用值（核心侧的约定，见 `AskOption.Label`）。
      op.textContent = o.label || o.value;
      sel.appendChild(op);
    }
    row.appendChild(sel);
    sel.value = "";
    return () => sel.value;
  }

  if (shape === "text") {
    const inp = document.createElement("textarea");
    inp.className = "asktext";
    inp.rows = 2;
    if (item.value) inp.value = item.value;
    row.appendChild(inp);
    return () => inp.value;
  }

  // `form` 与 `verbatim` 都是「若干命名框」——差别只在**标签**与**尺寸**。
  const fields = Array.isArray(item.fields) ? item.fields : [];
  const getters = [];

  for (const f of fields) {
    const wrap = document.createElement("label");
    wrap.className = "askfield";
    const nm = document.createElement("span");
    nm.textContent = f.label || f.name;
    wrap.appendChild(nm);

    const inp = document.createElement("textarea");
    // ⚠️ **`verbatim` 给足行数**：它装的是模型那段参数 JSON，一行看不出改了什么。
    inp.rows = shape === "verbatim" ? 6 : 1;
    if (shape === "verbatim") inp.className = "askverbatim";
    inp.value = f.value == null ? "" : String(f.value);
    wrap.appendChild(inp);

    row.appendChild(wrap);
    getters.push([f.name, () => inp.value]);
  }

  // ⚠️ `form` 的答案是**逐字段拼**的（键是工具自己定的名）；
  // 而 `verbatim` **只交那一段原文**（键固定是 `arguments`）——它要原样回传。
  return () => {
    const out = {};
    for (const [n, g] of getters) out[n] = g();
    return out;
  };
}

// 那一排按钮。⚠️ **审批按钮必须走 `commandNow`（等回执）**，不能像别处那样
// fire-and-forget：这条命令**可能被拒绝**（askId 过期、会话没在等），
// 而一句「点了没反应」在这里等价于「会话卡住了」。
function askButtons(specs, ask, inputs) {
  const bar = document.createElement("div");
  bar.className = "askactions";

  for (const spec of specs) {
    const b = document.createElement("button");
    b.textContent = spec.label;
    b.title = spec.why;     // 判据给人看：这颗按钮会做什么
    if (spec.deny) b.className = "deny";

    b.onclick = async () => {
      // 收集答案。⚠️ **`verbatim` 与 `form` 的取值函数返回对象**——摊平进 `answers`。
      const answers = {};
      for (const [id, get] of inputs) {
        if (id === "__rememberTool") continue;
        const v = get();
        if (v && typeof v === "object") Object.assign(answers, v);
        else if (typeof v === "string" && v.length > 0) answers[id] = v;
      }

      // ⚠️ **授权卡要有 `decision`**（2026-10-11 修）：核心只看这一个键。
      // ⚠️ 放在 `reason` **之前**塞——控制台也是这个次序，而 `reason` 是拒绝时
      // 才补的一格，两者不冲突（`decision` 说「批没批」，`reason` 说「为什么不批」）。
      if (spec.decision) answers.decision = spec.decision;

      // 拒绝时带上理由（可选）。⚠️ 理由会**落进记录**并回给模型——
      // 它比「档位不允许」有用得多（模型据此知道别再重试）。
      if (spec.deny) {
        const why = prompt("拒绝的理由（可留空）——它会告诉模型为什么");
        if (why && why.trim()) answers.reason = why.trim();
      }

      const remember = inputs.has("__rememberTool") ? inputs.get("__rememberTool")() : null;

      bar.querySelectorAll("button").forEach((x) => { x.disabled = true; });

      const frame = await commandNow("approval-" + crypto.randomUUID(), {
        kind: "approval.respond",
        sessionId: current,
        askId: ask.askId,
        outcome: spec.outcome,
        answers,
        // ⚠️ 只在真的勾了的时候才带上它（不带 = 只这一次）。
        ...(remember ? { remember } : {}),
      });

      if (!frame.accepted) {
        // ⚠️ **失败必须说出来**，而且要说在**这里**（卡片旁边）：时间线上那句会被
        // 随后的重拉冲掉，用户看到的就是「点了没反应，会话还卡着」。
        const err = document.createElement("div");
        err.className = "note bad";
        err.textContent = "没成功：" + (frame.reason || "核心没有回执");
        bar.appendChild(err);
        bar.querySelectorAll("button").forEach((x) => { x.disabled = false; });
        return;
      }

      // 成功：核心会续跑，事件会回来；重拉一次让卡片立刻消失（不等事件）。
      openSession(current);
    };

    bar.appendChild(b);
  }

  return bar;
}
