// Selene's "Report a problem": takes a report from Selene Core and opens a GitHub issue with it, so players can report a
// problem without a GitHub account. A report can also start in Selene and be finished on a phone (POST /draft, then the
// page at /r/<id> adds the words and sends it). Secrets and bindings (see wrangler.toml): APP_ID + APP_PRIVATE_KEY (a
// GitHub App with Issues read/write, installed on the repo only, so issues are opened by "<app>[bot]"; the key in PKCS#8
// PEM), or else GITHUB_TOKEN (fine-grained, Issues read/write; issues then carry that token owner's name), REPO
// ("owner/name"), LIMITS (KV, rate limits and the app's cached token), STORE (KV, pictures and drafts).

const CATEGORIES = [
  "Something doesn't work",
  "Something looks wrong",
  "Selene froze or closed",
  "Controller or navigation",
  "Sound or music",
  "Other",
];
const PLACES = [
  "Starting Playnite", "Spotlight", "Collection", "A game's page", "Store", "Captures", "Settings or menu",
  "Starting or playing a game", "Quitting", "Somewhere else",
];
const MAX_REQUEST = 12_000_000;   // bytes, logs, full-log bundle and picture included
const MAX_BUNDLE = 8_000_000;     // base64 characters: the gzip'd full logs (usually well under 1 MB)
const LOGS_KEEP = 365 * 24 * 3600; // seconds the full logs stay reachable from their issue
const MAX_PICTURE = 2_000_000;    // base64 characters
const MAX_BODY = 60_000;          // GitHub allows 65,536 characters in an issue body
const PER_IP_HOUR = 5;
const PER_DAY = 60;
const DRAFTS_PER_IP_HOUR = 10;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname;
    if (request.method === "GET" && path === "/") return text("Selene report service", 200);
    if (request.method === "GET" && path.startsWith("/shot/")) return picture(env, path.slice(6));
    if (request.method === "GET" && path.startsWith("/logs/")) return fullLogs(env, path.slice(6));
    if (request.method === "POST" && path === "/report") return report(request, env, url.origin);
    if (request.method === "POST" && path === "/draft") return draft(request, env, url.origin);
    const m = path.match(/^\/r\/([0-9a-f-]{36})(\/status)?$/);
    if (m && request.method === "GET" && m[2]) return status(env, m[1]);
    if (m && request.method === "GET") return phonePage(env, m[1]);
    if (m && request.method === "POST" && !m[2]) return finish(request, env, m[1], url.origin);
    return text("Not found", 404);
  },
};

// ---- straight from Selene ----
async function report(request, env, origin) {
  const r = await readReport(request);
  if (!r) return json({ ok: false, error: "bad request" }, 400);
  return send(r, request, env, origin);
}

// ---- started in Selene, finished on a phone ----
async function draft(request, env, origin) {
  const r = await readReport(request);
  if (!r) return json({ ok: false, error: "bad request" }, 400);
  const key = "drafts:" + (await ipKey(request));
  const count = Number((await env.LIMITS.get(key)) || 0);
  if (count >= DRAFTS_PER_IP_HOUR) return json({ ok: false, error: "slow down" }, 429);
  await env.LIMITS.put(key, String(count + 1), { expirationTtl: 3600 });
  const id = crypto.randomUUID();
  await env.STORE.put("draft:" + id, JSON.stringify(r), { expirationTtl: 3600 });
  return json({ ok: true, id, url: `${origin}/r/${id}` }, 200);
}

async function finish(request, env, id, origin) {
  const saved = await env.STORE.get("draft:" + id);
  if (!saved) return json({ ok: false, error: "gone" }, 404);
  let note = "";
  try { note = String((await request.json()).note || ""); } catch { return json({ ok: false, error: "bad request" }, 400); }
  const r = JSON.parse(saved);
  r.note = note.slice(0, 2000);
  const response = await send(r, request, env, origin);
  const result = await response.clone().json();
  if (result.ok) {
    await env.STORE.delete("draft:" + id);
    await env.STORE.put("done:" + id, JSON.stringify(result), { expirationTtl: 3600 });
  }
  return response;
}

async function status(env, id) {
  const done = await env.STORE.get("done:" + id);
  return json(done ? JSON.parse(done) : { ok: false, waiting: true }, 200);
}

// ---- the issue ----
async function send(r, request, env, origin) {
  // a few reports per address an hour, and a daily ceiling for the whole service
  const ipCountKey = "ip:" + (await ipKey(request));
  const dayKey = "day:" + new Date().toISOString().slice(0, 10);
  const [ipCount, dayCount] = await Promise.all([env.LIMITS.get(ipCountKey), env.LIMITS.get(dayKey)]);
  if (Number(ipCount || 0) >= PER_IP_HOUR || Number(dayCount || 0) >= PER_DAY) return json({ ok: false, error: "slow down" }, 429);

  let shotUrl = null;
  if (typeof r.screenshot === "string" && r.screenshot.length > 100 && r.screenshot.length <= MAX_PICTURE && r.screenshot.startsWith("/9j/")) {
    const id = crypto.randomUUID();
    await env.STORE.put("shot:" + id, base64ToBytes(r.screenshot));
    shotUrl = `${origin}/shot/${id}`;
  }

  // the full logs (Selene sends them gzip'd): kept here and linked from the issue, which only has room for a summary
  let logsUrl = null;
  if (typeof r.logBundle === "string" && r.logBundle.length > 20 && r.logBundle.length <= MAX_BUNDLE && r.logBundle.startsWith("H4sI")) {
    const id = crypto.randomUUID();
    await env.STORE.put("logs:" + id, base64ToBytes(r.logBundle), { expirationTtl: LOGS_KEEP });
    logsUrl = `${origin}/logs/${id}`;
  }

  const issue = build(r, shotUrl, logsUrl);
  const response = await fetch(`https://api.github.com/repos/${env.REPO}/issues`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${await githubToken(env)}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "selene-report-worker",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(issue),
  });
  if (!response.ok) {
    console.log("github", response.status, await response.text());
    return json({ ok: false, error: "github" }, 502);
  }
  const created = await response.json();
  await Promise.all([
    env.LIMITS.put(ipCountKey, String(Number(ipCount || 0) + 1), { expirationTtl: 3600 }),
    env.LIMITS.put(dayKey, String(Number(dayCount || 0) + 1), { expirationTtl: 172800 }),
  ]);
  return json({ ok: true, number: created.number, url: created.html_url }, 200);
}

// The token to open issues with: the GitHub App's installation token (cached for 50 of its 60 minutes) when the app is
// set up, else the personal GITHUB_TOKEN
async function githubToken(env) {
  if (!env.APP_ID || !env.APP_PRIVATE_KEY) return env.GITHUB_TOKEN;
  const cached = await env.LIMITS.get("app-token");
  if (cached) return cached;
  const headers = {
    Authorization: `Bearer ${await appJwt(env)}`,
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "selene-report-worker",
  };
  const install = await fetch(`https://api.github.com/repos/${env.REPO}/installation`, { headers });
  if (!install.ok) throw new Error("app installation: " + install.status + " " + (await install.text()));
  const { id } = await install.json();
  const res = await fetch(`https://api.github.com/app/installations/${id}/access_tokens`, { method: "POST", headers });
  if (!res.ok) throw new Error("app token: " + res.status + " " + (await res.text()));
  const { token } = await res.json();
  await env.LIMITS.put("app-token", token, { expirationTtl: 3000 });
  return token;
}

// a 9-minute JWT signed with the app's private key (RS256), what GitHub asks for to hand out installation tokens
async function appJwt(env) {
  const pem = env.APP_PRIVATE_KEY.replace(/-----[^-]+-----/g, "").replace(/\s+/g, "");
  const key = await crypto.subtle.importKey("pkcs8", base64ToBytes(pem), { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" }, false, ["sign"]);
  const now = Math.floor(Date.now() / 1000);
  const part = (o) => base64Url(new TextEncoder().encode(JSON.stringify(o)));
  const unsigned = part({ alg: "RS256", typ: "JWT" }) + "." + part({ iat: now - 60, exp: now + 540, iss: String(env.APP_ID) });
  const signature = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, new TextEncoder().encode(unsigned));
  return unsigned + "." + base64Url(new Uint8Array(signature));
}

function base64Url(bytes) {
  let text = "";
  for (const b of bytes) text += String.fromCharCode(b);
  return btoa(text).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function readReport(request) {
  const length = Number(request.headers.get("content-length") || 0);
  if (length > MAX_REQUEST) return null;
  try {
    const raw = await request.text();
    if (raw.length > MAX_REQUEST) return null;
    const r = JSON.parse(raw);
    if (!r || typeof r !== "object" || !CATEGORIES.includes(r.category)) return null;
    if (r.place != null && !PLACES.includes(r.place)) return null;
    return r;
  } catch {
    return null;
  }
}

function build(r, shotUrl, logsUrl) {
  const field = (v, max) => scrub(String(v ?? "").slice(0, max)).trim();
  const note = field(r.note, 2000);
  const where = field(r.place, 60);
  const tag = (v) => (v ? "`" + v.replace(/`/g, "'") + "`" : null);
  const lines = [
    `### ${r.category}${where ? " · " + where : ""}`,
    "",
    note ? note.split(/\r?\n/).map((l) => "> " + l).join("\n") : "> _No description given._",
    "",
  ];
  if (shotUrl) lines.push(`<img src="${shotUrl}" alt="The screen when this was reported" width="720">`, "");
  const selene = field(r.selene, 40), playnite = field(r.playnite, 40), windows = field(r.windows, 80);
  lines.push([tag(selene && "Selene " + selene), tag(playnite && "Playnite " + playnite), tag(windows)].filter(Boolean).join(" · "));
  if (logsUrl) lines.push("", `**Full logs:** ${logsUrl} (Selene and Playnite logs, add-ons, settings, displays)`);
  let body = lines.join("\n");
  // the logs fill what's left, newest lines kept
  const logs = [["Problems in selene.log", r.seleneProblems], ["selene.log, recent lines", r.seleneLog], ["playnite.log, warnings and errors", r.playniteLog]]
    .filter(([, v]) => typeof v === "string" && v.length);
  const room = Math.max(0, MAX_BODY - body.length - 700);
  const each = logs.length ? Math.floor(room / logs.length) : 0;
  if (logs.length) body += "\n";
  for (const [name, content] of logs) {
    let log = scrub(content).replace(/````/g, "'''");
    if (log.length > each) log = "(older lines cut)\n" + log.slice(log.length - each + 20);
    body += `\n<details><summary>${name}</summary>\n\n\`\`\`\`text\n${log}\n\`\`\`\`\n</details>\n`;
  }
  body += "\n<sub>Sent from Selene's Report a problem screen</sub>";
  // the title: what the player wrote (the category and place are in the body and the label)
  const short = note.replace(/\s+/g, " ").trim();
  const title = short ? (short.length > 80 ? short.slice(0, 77).trimEnd() + "..." : short) : `${r.category}${where ? " · " + where : ""}`;
  return { title, body, labels: ["bug"] };
}

// ---- the full logs, linked from the issue: plain text, stored gzip'd and sent as it is ----
async function fullLogs(env, id) {
  if (!/^[0-9a-f-]{36}$/.test(id)) return text("Not found", 404);
  const bytes = await env.STORE.get("logs:" + id, "arrayBuffer");
  if (!bytes) return text("Not found (full logs are kept for a year)", 404);
  return new Response(bytes, {
    encodeBody: "manual",
    headers: { "Content-Type": "text/plain; charset=utf-8", "Content-Encoding": "gzip", "Cache-Control": "public, max-age=86400" },
  });
}

// ---- the picture, shown in the issue ----
async function picture(env, id) {
  if (!/^[0-9a-f-]{36}$/.test(id)) return text("Not found", 404);
  const bytes = await env.STORE.get("shot:" + id, "arrayBuffer");
  if (!bytes) return text("Not found", 404);
  return new Response(bytes, { headers: { "Content-Type": "image/jpeg", "Cache-Control": "public, max-age=31536000, immutable" } });
}

// ---- the phone page ----
async function phonePage(env, id) {
  const saved = await env.STORE.get("draft:" + id);
  const done = saved ? null : await env.STORE.get("done:" + id);
  const r = saved ? JSON.parse(saved) : null;
  const state = JSON.stringify({ id, category: r ? r.category : null, place: r ? r.place : null, done: done ? JSON.parse(done) : null });
  return new Response(PAGE.replace("__MARK__", JSON.stringify(MARK)).replace("__STATE__", state.replace(/</g, "\\u003c")), { headers: { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" } });
}

const MARK = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="14 24 72 72" aria-hidden="true"><path fill="currentColor" d="M23.109 44.577 A31 31 0 1 0 76.891 44.577 A27 27 0 1 1 23.109 44.577 Z"/><circle cx="50" cy="42" r="9" fill="none" stroke="currentColor" stroke-width="3.2"/><circle cx="50" cy="42" r="3.2" fill="currentColor"/></svg>`;

const PAGE = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0a0f1f">
<title>Report a problem · Selene</title>
<style>
  :root { color-scheme: dark; --bg: #0c0f17; --ink: #e9ecf3; --soft: #9aa2b4; --faint: #6c7486; --field: #141824; --line: #262c3b; --focus: #c9d3e8; }
  * { box-sizing: border-box; }
  body { margin: 0; min-height: 100vh; font: 17px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; color: var(--ink); background: var(--bg); }
  main { max-width: 520px; margin: 0 auto; padding: 40px 22px calc(36px + env(safe-area-inset-bottom)); }
  .brand { display: flex; justify-content: center; margin-bottom: 30px; }
  .mark { position: relative; width: 52px; height: 52px; color: var(--ink); }
  .mark::before { content: ""; position: absolute; left: 50%; top: 50%; width: 100px; height: 100px; transform: translate(-50%, -50%); border-radius: 50%;
                  background: radial-gradient(circle, rgba(185, 202, 255, .28) 0%, rgba(150, 175, 255, .10) 38%, rgba(150, 175, 255, 0) 70%); pointer-events: none;
                  animation: breathe 2.2s ease-in-out infinite alternate; }
  @keyframes breathe { from { opacity: .45; } to { opacity: 1; } }
  .mark svg { position: relative; width: 100%; height: 100%; display: block; overflow: visible; filter: drop-shadow(0 0 6px rgba(210, 222, 255, .6)); }
  h1 { font-size: 26px; font-weight: 600; margin: 0 0 6px; }
  .sub { color: var(--soft); margin: 0 0 26px; }
  .about { font-size: 13px; font-weight: 600; letter-spacing: .08em; text-transform: uppercase; color: var(--faint); margin: 0 0 10px; }
  textarea { width: 100%; min-height: 190px; resize: vertical; border-radius: 10px; border: 1px solid var(--line);
             background: var(--field); color: inherit; font: inherit; padding: 14px 16px; outline: none; }
  textarea:focus { border-color: var(--focus); }
  textarea::placeholder { color: var(--faint); }
  button { margin-top: 16px; width: 100%; border: 0; border-radius: 10px; padding: 15px; font: inherit; font-weight: 600; cursor: pointer;
           background: var(--ink); color: var(--bg); }
  button:active { opacity: .85; }
  button:disabled { opacity: .55; }
  .note { color: var(--faint); font-size: 14px; margin: 18px 0 0; }
  .done { text-align: center; padding-top: 10px; }
  .check { width: 72px; height: 72px; border-radius: 50%; background: var(--ink); color: var(--bg); display: grid; place-items: center;
           margin: 6px auto 22px; }
  a.link { color: var(--ink); }
</style></head>
<body><main id="app"></main>
<script>
const state = __STATE__;
const MARK = __MARK__;
const app = document.getElementById("app");
function el(tag, cls, text) { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
function checkMark() { const c = el("div", "check"); c.innerHTML = '<svg viewBox="0 0 24 24" width="34" height="34" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7.5" fill="none" stroke="currentColor" stroke-width="2.8" stroke-linecap="round" stroke-linejoin="round"/></svg>'; return c; }
function brand() { const b = el("div", "brand"); const m = el("div", "mark"); m.innerHTML = MARK; b.append(m); return b; }
function showDone(result) {
  app.replaceChildren();
  const box = el("div", "done");
  box.append(brand(), checkMark(), el("h1", null, "Report #" + result.number + " is sent"),
             el("p", "sub", "Thanks, it helps make Selene better. You can close this page."));
  const p = el("p", "note"); const a = el("a", "link", "See it on GitHub"); a.href = result.url;
  p.append(a, document.createTextNode(". Adding screenshots or comments there needs a free account."));
  box.append(p);
  app.append(box);
}
if (state.done) showDone(state.done);
else if (!state.category) {
  app.append(brand(), el("h1", null, "This report has expired"), el("p", "sub", "Start it again from Selene: Settings, Advanced, Report a problem."));
} else {
  app.append(brand(), el("h1", null, "Report a problem"), el("p", "sub", "Tell us what happened. Selene sends it with your logs, no account needed."));
  const about = el("p", "about", state.category + (state.place ? "  \u00b7  " + state.place : ""));
  const box = document.createElement("textarea"); box.placeholder = "What happened? What did you expect instead?"; box.maxLength = 2000;
  const send = el("button", null, "Send report");
  send.onclick = async () => {
    send.disabled = true; send.textContent = "Sending...";
    try {
      const res = await fetch(location.pathname, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ note: box.value }) });
      const result = await res.json();
      if (result.ok) return showDone(result);
      send.textContent = result.error === "slow down" ? "Too many reports for now, try again later" : result.error === "gone" ? "This report has expired" : "Couldn't send, try again";
    } catch { send.textContent = "Couldn't send, try again"; }
    send.disabled = false;
  };
  app.append(about, box, send, el("p", "note", "It shows up publicly on GitHub, with names, folders and emails taken out of the logs."));
  box.focus();
}
</script></body></html>`;

// ---- helpers ----
// no names or home folders in a public issue
function scrub(s) {
  return s
    .replace(/([A-Za-z]:\\Users\\)[^\\\/\r\n"']+/gi, "$1<user>")
    .replace(/([A-Za-z]:\/Users\/)[^\\\/\r\n"']+/gi, "$1<user>")
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, "<email>");
}


function base64ToBytes(b64) {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes;
}

async function ipKey(request) {
  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  return (await sha256(ip)).slice(0, 32);
}

async function sha256(s) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(s));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function json(obj, status) {
  return new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json" } });
}

function text(s, status) {
  return new Response(s, { status, headers: { "Content-Type": "text/plain" } });
}
