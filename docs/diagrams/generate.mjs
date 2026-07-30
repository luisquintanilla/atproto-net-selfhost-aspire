// Generates the atproto-net-selfhost-aspire documentation diagrams as self-contained SVGs.
//
// One script = one visual language. Every diagram shares a palette, a component color code
// (PDS = green, Relay/firehose = purple, AppView = blue, you/identity = amber, browser = teal),
// card/arrow primitives, and a dark "figure card" background so the images read the same on
// GitHub light or dark. Pure Node (no dependencies); run `node docs/diagrams/generate.mjs`
// from the repo root to (re)build everything into docs/img/.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..", "..");
const OUT = join(ROOT, "docs", "img");
mkdirSync(OUT, { recursive: true });

// ---- palette + component color code -------------------------------------------------
const C = {
  bg: "#0f1220", panel: "#171a2b", panel2: "#1e2238", line: "#2a2f4a",
  text: "#e7e9f3", muted: "#9aa0bd",
  you: "#e0b341",     // amber  — you / identity
  pds: "#4ec97a",     // green  — PDS / your data
  relay: "#c678dd",   // purple — relay / firehose / the wire
  appview: "#7aa2ff", // blue   — appview / the reader
  browser: "#56b6c2", // teal   — browser / board
};
const MONO = "ui-monospace,SFMono-Regular,Menlo,Consolas,monospace";
const SANS = "system-ui,-apple-system,Segoe UI,Roboto,sans-serif";

const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

// A marker (arrowhead) per accent color so edges inherit their component's color.
const MARK = { line: C.line, muted: C.muted, you: C.you, pds: C.pds, relay: C.relay, appview: C.appview, browser: C.browser };
const HEX2KEY = Object.fromEntries(Object.entries(MARK).map(([k, v]) => [v, k]));
// Accept either a color key ("pds") or a hex value (C.pds) and resolve to a marker key.
const colorKey = (c) => (MARK[c] ? c : HEX2KEY[c] || "muted");
const markers = () =>
  Object.entries(MARK).map(([k, v]) =>
    `<marker id="a-${k}" viewBox="0 0 10 10" refX="8.5" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M0 0 L10 5 L0 10 z" fill="${v}"/></marker>`).join("");

function frame(w, h, inner, title) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" font-family="${SANS}">
  <defs>
    ${markers()}
    <filter id="sh" x="-30%" y="-30%" width="160%" height="160%"><feDropShadow dx="0" dy="2" stdDeviation="3" flood-color="#000" flood-opacity="0.38"/></filter>
  </defs>
  <rect x="0.75" y="0.75" width="${w - 1.5}" height="${h - 1.5}" rx="16" fill="${C.bg}" stroke="${C.line}" stroke-width="1.5"/>
  ${title ? `<text x="26" y="34" fill="${C.muted}" font-size="12.5" letter-spacing="0.09em" font-weight="600">${esc(title.toUpperCase())}</text>` : ""}
  ${inner}
</svg>`;
}

// A rounded component card with a colored spine + optional emoji chip, title and subtitle.
function card(x, y, w, h, { color = C.line, icon = "", title = "", sub = "", fill = C.panel, titleSize = 15 } = {}) {
  const tx = x + (icon ? 46 : 16);
  const ty = sub ? y + h / 2 - 5 : y + h / 2 + 5;
  return `<g filter="url(#sh)">
    <rect x="${x}" y="${y}" width="${w}" height="${h}" rx="12" fill="${fill}" stroke="${color}" stroke-width="1.5"/>
    <rect x="${x}" y="${y}" width="6" height="${h}" rx="3" fill="${color}"/>
  </g>
  ${icon ? `<text x="${x + 27}" y="${y + h / 2 + 1}" font-size="24" text-anchor="middle" dominant-baseline="central">${icon}</text>` : ""}
  <text x="${tx}" y="${ty}" fill="${C.text}" font-size="${titleSize}" font-weight="650">${esc(title)}</text>
  ${sub ? wrap(tx, y + h / 2 + 13, w - (icon ? 58 : 28), sub, C.muted, 12, 15) : ""}`;
}

// Naive word-wrap into <text>/<tspan> lines (approx 6.1px/char at 12px).
function wrap(x, y, wpx, text, fill, size = 12, lh = 15) {
  const cpl = Math.max(6, Math.floor(wpx / (size * 0.52)));
  const words = String(text).split(" ");
  const lines = [];
  let cur = "";
  for (const w of words) {
    if ((cur + " " + w).trim().length > cpl) { if (cur) lines.push(cur); cur = w; }
    else cur = (cur + " " + w).trim();
  }
  if (cur) lines.push(cur);
  return `<text x="${x}" y="${y}" fill="${fill}" font-size="${size}">${lines.map((l, i) => `<tspan x="${x}" dy="${i ? lh : 0}">${esc(l)}</tspan>`).join("")}</text>`;
}

function arrow(x1, y1, x2, y2, { color = "muted", label = "", dash = false, width = 2 } = {}) {
  const key = colorKey(color);
  const stroke = MARK[key];
  const line = `<path d="M${x1} ${y1} L${x2} ${y2}" fill="none" stroke="${stroke}" stroke-width="${width}" ${dash ? 'stroke-dasharray="5 4"' : ""} marker-end="url(#a-${key})"/>`;
  if (!label) return line;
  const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
  const wpx = label.length * 6.6 + 14;
  return line + `<g><rect x="${mx - wpx / 2}" y="${my - 11}" width="${wpx}" height="18" rx="5" fill="${C.panel2}" stroke="${C.line}"/><text x="${mx}" y="${my + 2.5}" fill="${C.text}" font-size="11.5" text-anchor="middle">${esc(label)}</text></g>`;
}

function pill(x, y, text, color) {
  const wpx = text.length * 6.6 + 18;
  return `<g><rect x="${x}" y="${y}" width="${wpx}" height="22" rx="11" fill="${C.panel2}" stroke="${color}"/><text x="${x + wpx / 2}" y="${y + 15}" fill="${C.text}" font-size="12" text-anchor="middle">${esc(text)}</text></g>`;
}

// A small color legend (component code) shown at the foot of overview diagrams.
function legend(x, y, items) {
  let cx = x;
  return items.map(([label, color]) => {
    const g = `<circle cx="${cx + 6}" cy="${y}" r="6" fill="${color}"/><text x="${cx + 18}" y="${y + 4}" fill="${C.muted}" font-size="12">${esc(label)}</text>`;
    cx += 18 + label.length * 6.4 + 22;
    return g;
  }).join("");
}

function underbracket(x1, x2, y, color) {
  const mid = (x1 + x2) / 2;
  return `<path d="M${x1} ${y} L${x1} ${y + 6} L${mid - 6} ${y + 6} L${mid} ${y + 13} L${mid + 6} ${y + 6} L${x2} ${y + 6} L${x2} ${y}" fill="none" stroke="${color}" stroke-width="2"/>`;
}

// ---- sequence-diagram helper --------------------------------------------------------
// participants: [{ name, color, icon }]; messages: [{ from, to, label, color?, dashed?, note? }]
// `from`/`to` are participant indexes; a message with note===true draws a self box.
function sequence(w, title, participants, messages, { headTop = 58, headH = 46, step = 56 } = {}) {
  const n = participants.length;
  const margin = 100;
  const usable = w - margin * 2;
  const xs = participants.map((_, i) => (n === 1 ? w / 2 : margin + (usable * i) / (n - 1)));
  const boxW = Math.min(150, (usable / n) - 8);

  const top = headTop + headH + 26;
  let y = top;
  const rows = [];
  messages.forEach((m, idx) => {
    const num = `<circle cx="${34}" cy="${y - 4}" r="11" fill="${C.panel2}" stroke="${C.line}"/><text x="34" y="${y - 0.5}" fill="${C.text}" font-size="11.5" text-anchor="middle">${idx + 1}</text>`;
    if (m.note || m.from === m.to) {
      const bw = 168, bh = 34;
      const cx = Math.max(bw / 2 + 52, Math.min(w - bw / 2 - 14, xs[m.from]));
      rows.push(`<g filter="url(#sh)"><rect x="${cx - bw / 2}" y="${y - bh / 2 - 2}" width="${bw}" height="${bh}" rx="8" fill="${C.panel2}" stroke="${participants[m.from].color}"/></g>` +
        wrapCentered(cx, y - 2, bw - 14, m.label, C.text, 11.5) + num);
      y += bh + 16;
    } else {
      const x1 = xs[m.from], x2 = xs[m.to];
      const dir = x2 > x1 ? -8 : 8;
      rows.push(num + arrow(x1 + (x2 > x1 ? 4 : -4), y, x2 + dir, y, { color: m.color || participants[m.from].color, label: m.label, dash: m.dashed }));
      y += step - 8;
    }
  });
  const bottom = y + 6;

  const lifelines = xs.map((x, i) =>
    `<line x1="${x}" y1="${headTop + headH}" x2="${x}" y2="${bottom}" stroke="${C.line}" stroke-width="1.5" stroke-dasharray="3 5"/>` +
    headBox(x - boxW / 2, headTop, boxW, headH, participants[i])).join("");

  return frame(w, bottom + 20, lifelines + rows.join(""), title);
}
function headBox(x, y, w, h, p) {
  return `<g filter="url(#sh)"><rect x="${x}" y="${y}" width="${w}" height="${h}" rx="10" fill="${C.panel}" stroke="${p.color}" stroke-width="1.5"/></g>` +
    `${p.icon ? `<text x="${x + 20}" y="${y + h / 2 + 1}" font-size="19" text-anchor="middle" dominant-baseline="central">${p.icon}</text>` : ""}` +
    `<text x="${x + (p.icon ? 36 : w / 2)}" y="${y + h / 2 + 4}" fill="${C.text}" font-size="13" font-weight="650" text-anchor="${p.icon ? "start" : "middle"}">${esc(p.name)}</text>`;
}
function wrapCentered(cx, y, wpx, text, fill, size) {
  const cpl = Math.max(8, Math.floor(wpx / (size * 0.52)));
  const words = String(text).split(" ");
  const lines = []; let cur = "";
  for (const w of words) { if ((cur + " " + w).trim().length > cpl) { if (cur) lines.push(cur); cur = w; } else cur = (cur + " " + w).trim(); }
  if (cur) lines.push(cur);
  const y0 = y - ((lines.length - 1) * 14) / 2;
  return `<text x="${cx}" y="${y0}" fill="${fill}" font-size="${size}" text-anchor="middle">${lines.map((l, i) => `<tspan x="${cx}" dy="${i ? 14 : 0}">${esc(l)}</tspan>`).join("")}</text>`;
}

// ======================================================================================
// Diagrams
// ======================================================================================
const D = {};

// 1. README hero — title band + the five-hop flow with the running example.
D["hero"] = () => {
  const w = 960, h = 300;
  const y = 150;
  const nodes = [
    { x: 40, icon: "🧑", t: "You", s: "pick a status", c: C.you },
    { x: 220, icon: "🏠", t: "Your PDS", s: "writes + signs", c: C.pds },
    { x: 400, icon: "📡", t: "Relay", s: "one firehose", c: C.relay },
    { x: 580, icon: "📰", t: "AppView", s: "indexes it", c: C.appview },
    { x: 760, icon: "🖥️", t: "Board", s: "everyone sees", c: C.browser },
  ];
  let b = `<text x="30" y="70" fill="${C.text}" font-size="30" font-weight="750">atproto, self-hosted in .NET</text>
  <text x="31" y="98" fill="${C.muted}" font-size="15">Run your own PDS, Relay, and AppView. Watch one 🌤 flow across all three, live.</text>`;
  nodes.forEach((nd, i) => {
    b += card(nd.x, y, 150, 66, { color: nd.c, icon: nd.icon, title: nd.t, sub: nd.s });
    if (i < nodes.length - 1) b += arrow(nd.x + 150, y + 33, nodes[i + 1].x, y + 33, { color: nodes[i + 1].c });
  });
  b += `<text x="52" y="${y - 16}" font-size="20">🌤</text>`;
  b += legend(30, 262, [["PDS", C.pds], ["Relay / firehose", C.relay], ["AppView", C.appview], ["you / identity", C.you], ["board", C.browser]]);
  return frame(w, h, b);
};

// 2. The email + newswire + newspaper analogy.
D["analogy"] = () => {
  const w = 940, h = 340, y = 120, cw = 250, ch = 150;
  const cards = [
    { x: 30, icon: "📧", t: "Your PDS", real: "Personal Data Server", s: "Like your email provider. Your data lives here; switch providers and keep your address.", c: C.pds },
    { x: 345, icon: "📡", t: "The Relay", real: "aggregator", s: "Like a newswire. It gathers everyone's public activity into one feed, the firehose.", c: C.relay },
    { x: 660, icon: "📰", t: "The AppView", real: "index / app", s: "Like a newspaper. It reads the wire and builds something you can actually browse.", c: C.appview },
  ];
  let b = `<text x="30" y="70" fill="${C.text}" font-size="21" font-weight="700">atproto ≈ email + a newswire + a newspaper</text>
  <text x="31" y="94" fill="${C.muted}" font-size="13.5">Three roles you already understand. The real names are in the cards.</text>`;
  cards.forEach((cd, i) => {
    b += `<g filter="url(#sh)"><rect x="${cd.x}" y="${y}" width="${cw}" height="${ch}" rx="14" fill="${C.panel}" stroke="${cd.c}" stroke-width="1.5"/><rect x="${cd.x}" y="${y}" width="${cw}" height="6" rx="3" fill="${cd.c}"/></g>
    <text x="${cd.x + 24}" y="${y + 52}" font-size="30">${cd.icon}</text>
    <text x="${cd.x + 70}" y="${y + 44}" fill="${C.text}" font-size="17" font-weight="700">${esc(cd.t)}</text>
    <text x="${cd.x + 70}" y="${y + 62}" fill="${cd.c}" font-size="11.5">${esc(cd.real)}</text>
    ${wrap(cd.x + 22, y + 92, cw - 40, cd.s, C.muted, 12.5, 17)}`;
    if (i < cards.length - 1) b += arrow(cd.x + cw, y + ch / 2, cards[i + 1].x, y + ch / 2, { color: cards[i + 1].c, label: "firehose" });
  });
  b += `<text x="30" y="312" fill="${C.muted}" font-size="12.5">Your data flows left → right: it is created once on your PDS, aggregated by the Relay, and read by any number of AppViews.</text>`;
  return frame(w, h, b);
};

// 3. The "usual shape" of an atproto app.
D["usual-shape"] = () => {
  const w = 940, h = 340;
  let b = `<text x="30" y="66" fill="${C.text}" font-size="20" font-weight="700">The usual shape of an atproto app</text>`;
  const pdss = [
    { x: 40, y: 100, name: "alice.pds", },
    { x: 40, y: 168, name: "bob.pds" },
    { x: 40, y: 236, name: "carol.pds" },
  ];
  pdss.forEach((p) => { b += card(p.x, p.y, 190, 52, { color: C.pds, icon: "🏠", title: p.name, sub: "repo + firehose" }); });
  b += card(360, 150, 190, 90, { color: C.relay, icon: "📡", title: "Relay", sub: "aggregates every PDS into one firehose" });
  b += card(690, 150, 210, 90, { color: C.appview, icon: "📰", title: "AppView", sub: "indexes the firehose for clients" });
  pdss.forEach((p) => { b += arrow(230, p.y + 26, 360, 195, { color: C.pds, dashed: true }); });
  b += arrow(550, 195, 690, 195, { color: C.appview, label: "subscribeRepos" });
  b += pill(360, 262, "each user hosts their own data (PDS)", C.pds);
  b += legend(30, 315, [["users / PDSes", C.pds], ["relay / firehose", C.relay], ["appview", C.appview]]);
  return frame(w, h, b);
};

// 4. The life of an emoji — a numbered step strip.
D["life-of-an-emoji"] = () => {
  const w = 980, h = 250, y = 92, bw = 168, bh = 96, gap = (w - 40 - bw * 5) / 4;
  const steps = [
    { icon: "🧑", t: "You pick 🌤", s: "click a status", c: C.you },
    { icon: "🏠", t: "PDS writes it", s: "signs a commit", c: C.pds },
    { icon: "📢", t: "#commit", s: "onto the firehose", c: C.pds },
    { icon: "📡", t: "Relay", s: "adds a global seq", c: C.relay },
    { icon: "📰", t: "AppView", s: "latest-wins", c: C.appview },
  ];
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">The life of an emoji</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">One 🌤, from your click to everyone's board.</text>`;
  steps.forEach((st, i) => {
    const x = 20 + i * (bw + gap);
    b += `<g filter="url(#sh)"><rect x="${x}" y="${y}" width="${bw}" height="${bh}" rx="12" fill="${C.panel}" stroke="${st.c}" stroke-width="1.5"/><rect x="${x}" y="${y}" width="${bw}" height="6" rx="3" fill="${st.c}"/></g>
    <circle cx="${x + 18}" cy="${y + 26}" r="12" fill="${C.panel2}" stroke="${st.c}"/><text x="${x + 18}" y="${y + 30}" fill="${C.text}" font-size="12" text-anchor="middle">${i + 1}</text>
    <text x="${x + bw - 18}" y="${y + 34}" font-size="24" text-anchor="middle">${st.icon}</text>
    <text x="${x + 16}" y="${y + 60}" fill="${C.text}" font-size="14" font-weight="650">${esc(st.t)}</text>
    ${wrap(x + 16, y + 78, bw - 28, st.s, C.muted, 12, 14)}`;
    if (i < steps.length - 1) b += arrow(x + bw, y + bh / 2, x + bw + gap, y + bh / 2, { color: steps[i + 1].c });
  });
  b += `<text x="20" y="${y + bh + 40}" fill="${C.muted}" font-size="13">6. Every open board flashes 🌤 within a quarter second — pushed live over SignalR.</text>`;
  return frame(w, h, b);
};

// 5. AT-URI anatomy.
D["at-uri"] = () => {
  const w = 940, h = 224;
  const fs = 20, cwid = 12.05, sx = 40, ty = 108;
  const uri = [
    ["at://", C.muted], ["did:web:localhost%3A5271:pds:demo1", C.you], ["/", C.muted],
    ["place.selfhost.status", C.appview], ["/", C.muted], ["self", C.pds],
  ];
  let x = sx, spans = "";
  const seg = {};
  uri.forEach(([txt, col], i) => { const start = x; spans += `<tspan fill="${col}">${esc(txt)}</tspan>`; x += txt.length * cwid; seg[i] = [start, x]; });
  let b = `<text x="30" y="64" fill="${C.text}" font-size="20" font-weight="700">One address for one record: the AT-URI</text>
  <text x="${sx}" y="${ty}" font-family="${MONO}" font-size="${fs}" font-weight="600">${spans}</text>`;
  const marks = [["who", seg[1], C.you], ["what", seg[3], C.appview], ["which", seg[5], C.pds]];
  marks.forEach(([short, [x1, x2], col]) => {
    b += underbracket(x1, x2, ty + 12, col);
    b += `<text x="${(x1 + x2) / 2}" y="${ty + 38}" fill="${col}" font-size="14" font-weight="700" text-anchor="middle">${esc(short)}</text>`;
  });
  b += `<text x="40" y="${ty + 74}" font-size="12.5"><tspan fill="${C.you}" font-weight="600">who</tspan><tspan fill="${C.muted}"> = your identity (DID)   ·   </tspan><tspan fill="${C.appview}" font-weight="600">what</tspan><tspan fill="${C.muted}"> = record type (collection / NSID)   ·   </tspan><tspan fill="${C.pds}" font-weight="600">which</tspan><tspan fill="${C.muted}"> = this record (rkey)</tspan></text>`;
  return frame(w, h, b);
};

// 6. Identity you own (DID / handle / PDS portability).
D["identity"] = () => {
  const w = 940, h = 330;
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">Identity you own</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Like a phone number you port between carriers. Your DID never changes; your PDS can.</text>`;
  b += card(70, 130, 200, 60, { color: C.text, icon: "🏷️", title: "@demo1.pds…", sub: "handle (friendly name)" });
  b += card(370, 122, 220, 76, { color: C.you, icon: "🔑", title: "did:web:…:demo1", sub: "the number you own (DID)" });
  b += card(700, 110, 190, 56, { color: C.pds, icon: "🏠", title: "PDS A", sub: "where the repo lives today" });
  b += card(700, 200, 190, 56, { color: C.pds, icon: "🏠", title: "PDS B", sub: "move here, keep your DID" });
  b += arrow(270, 160, 370, 160, { color: "you", label: "points to" });
  b += arrow(590, 150, 700, 138, { color: C.pds, label: "hosted on" });
  b += arrow(590, 168, 700, 224, { color: C.pds, dashed: true, label: "port" });
  b += `<text x="30" y="304" fill="${C.muted}" font-size="12.5">The handle can change and the PDS can change; the DID is the stable identity everything else resolves through.</text>`;
  return frame(w, h, b);
};

// 7. Repo as a signed, git-like log.
D["repo-as-git"] = () => {
  const w = 940, h = 330;
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">Your repo is a signed, git-like log</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">A content-addressed tree of records. getRepo hands you the whole thing as a CAR file.</text>`;
  const row = 118, bh = 66;
  b += card(30, row, 180, bh, { color: C.pds, icon: "📦", title: "Repo", sub: "all your records" });
  b += card(250, row, 180, bh, { color: C.pds, icon: "🔏", title: "Signed commit", sub: "root + signature" });
  b += card(470, row, 170, bh, { color: C.pds, icon: "🌳", title: "MST", sub: "Merkle search tree" });
  b += card(680, row, 220, bh, { color: C.appview, icon: "📄", title: "records", sub: "collection / rkey → value" });
  b += arrow(210, row + bh / 2, 250, row + bh / 2, { color: C.pds });
  b += arrow(430, row + bh / 2, 470, row + bh / 2, { color: C.pds });
  b += arrow(640, row + bh / 2, 680, row + bh / 2, { color: C.appview });
  // git analogy row
  const gy = 236;
  const gits = [
    ["Commit ≈ git commit", 30], ["MST ≈ git's Merkle tree", 250], ["CID ≈ object id (hash)", 470], ["CAR ≈ git bundle/clone", 690],
  ];
  b += `<text x="30" y="${gy}" fill="${C.muted}" font-size="12.5" letter-spacing="0.06em">IF YOU KNOW GIT</text>`;
  gits.forEach(([t, x]) => { b += pill(x, gy + 12, t, C.line); });
  b += `<text x="30" y="${gy + 66}" fill="${C.muted}" font-size="12.5">Every node has a CID (a content hash), so the whole structure is tamper-evident — change one byte and every CID above it changes.</text>`;
  return frame(w, h, b);
};

// 8. Topology / component graph with protocols on the edges.
D["topology"] = () => {
  const w = 940, h = 402;
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">Topology</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Who talks to whom, and over what.</text>`;
  b += card(360, 104, 220, 62, { color: C.browser, icon: "🖥️", title: "Browser board", sub: "static SPA + SignalR" });
  b += card(360, 214, 220, 62, { color: C.appview, icon: "📰", title: "AppView", sub: "ingest · Rx · store · hub" });
  b += card(60, 300, 200, 60, { color: C.relay, icon: "📡", title: "Relay", sub: "aggregated firehose" });
  b += card(680, 300, 200, 60, { color: C.pds, icon: "🏠", title: "PDS", sub: "repos + firehose" });
  b += arrow(470, 166, 470, 214, { color: C.appview, label: "XRPC + SignalR (http)" });
  b += arrow(360, 250, 260, 315, { color: C.relay, label: "subscribeRepos (ws)" });
  b += arrow(260, 340, 680, 340, { color: C.relay, label: "crawl firehose (ws)" });
  b += arrow(580, 250, 700, 300, { color: C.pds, label: "inspect / compose (http)" });
  b += legend(30, 384, [["browser", C.browser], ["appview", C.appview], ["relay", C.relay], ["pds", C.pds]]);
  return frame(w, h, b);
};

// 9. Write path (sequence).
D["write-path"] = () =>
  sequence(900, "Write path — composing a status",
    [
      { name: "Browser", color: C.browser, icon: "🖥️" },
      { name: "AppView", color: C.appview, icon: "📰" },
      { name: "PDS", color: C.pds, icon: "🏠" },
      { name: "Relay", color: C.relay, icon: "📡" },
    ],
    [
      { from: 0, to: 1, label: "POST /compose { status: 🌤 }" },
      { from: 1, to: 2, label: "putRecord (place.selfhost.status/self)" },
      { from: 2, to: 2, note: true, label: "update MST · sign commit · write CAR" },
      { from: 2, to: 3, label: "#commit frame (ws)", color: C.pds },
      { from: 3, to: 1, label: "#commit + global seq (ws)", color: C.relay },
      { from: 1, to: 1, note: true, label: "ingest → Rx projection → store (latest-wins)" },
      { from: 1, to: 0, label: "SignalR: presence delta → board flashes 🌤", color: C.appview },
    ]);

// 10. Read / inspect path (sequence).
D["read-path"] = () =>
  sequence(860, "Read path — inspecting the real record",
    [
      { name: "Browser", color: C.browser, icon: "🖥️" },
      { name: "AppView", color: C.appview, icon: "📰" },
      { name: "PDS", color: C.pds, icon: "🏠" },
    ],
    [
      { from: 0, to: 1, label: "click a row → GET /inspect/repo?did=…" },
      { from: 1, to: 1, note: true, label: "resolve DID → did.json → PDS endpoint" },
      { from: 1, to: 2, label: "listRecords + getLatestCommit" },
      { from: 2, to: 1, label: "records + commit + CID", color: C.pds },
      { from: 1, to: 0, label: "{ did, handle, pds, commit, records[] }", color: C.appview },
      { from: 0, to: 0, note: true, label: "render inspector drawer (JSON · CID · AT-URI · CAR)" },
    ]);

// 11. Identity resolution (sequence).
D["identity-resolution"] = () =>
  sequence(820, "Identity resolution — DID → PDS",
    [
      { name: "AppView", color: C.appview, icon: "📰" },
      { name: "did.json", color: C.you, icon: "🔑" },
      { name: "PDS", color: C.pds, icon: "🏠" },
    ],
    [
      { from: 0, to: 0, note: true, label: "have DID did:web:…:demo1" },
      { from: 0, to: 1, label: "GET /pds/demo1/did.json" },
      { from: 1, to: 0, label: "DID document", color: C.you },
      { from: 0, to: 0, note: true, label: "read #atproto_pds serviceEndpoint" },
      { from: 0, to: 2, label: "XRPC calls (getRecord, listRecords, getRepo)" },
    ]);

// 12. Data model graph.
D["data-model"] = () => {
  const w = 900, h = 392;
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">atproto data model</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Everything hangs off a signed commit and is addressed by CID.</text>`;
  b += card(360, 100, 180, 56, { color: C.pds, icon: "🔏", title: "Commit", sub: "signed root" });
  b += card(360, 190, 180, 56, { color: C.pds, icon: "🌳", title: "MST root", sub: "Merkle tree" });
  b += card(120, 280, 220, 54, { color: C.appview, icon: "📄", title: "place.selfhost.status/self", sub: "record → value + CID" });
  b += card(560, 280, 220, 54, { color: C.appview, icon: "📄", title: "…/other collections", sub: "record → value + CID" });
  b += card(680, 100, 190, 56, { color: C.relay, icon: "🗜️", title: "CAR", sub: "blocks by CID" });
  b += arrow(450, 156, 450, 190, { color: C.pds });
  b += arrow(410, 246, 250, 280, { color: C.appview });
  b += arrow(490, 246, 660, 280, { color: C.appview });
  b += arrow(540, 128, 680, 128, { color: C.relay, label: "getRepo exports" });
  b += `<text x="30" y="368" fill="${C.muted}" font-size="12.5">A CID is a hash of the content it names, so identical content shares a CID and any change is detectable.</text>`;
  return frame(w, h, b);
};

// 13. Reactive layering — pull ingest → IObservable seam → Rx push.
D["reactive-layering"] = () => {
  const w = 980, h = 308, y = 118, bh = 96;
  const lanes = [
    { x: 24, w: 150, icon: "📡", t: "Firehose", s: "WebSocket frames", c: C.relay },
    { x: 200, w: 196, icon: "⤵️", t: "Pull ingest", s: "IAsyncEnumerable + bounded Channel (Wait)", c: C.pds },
    { x: 422, w: 196, icon: "🔌", t: "IObservable seam", s: "no Rx dependency here", c: C.muted },
    { x: 644, w: 150, icon: "⚙️", t: "Rx projection", s: "GroupBy · Replay(1) · Buffer", c: C.appview },
    { x: 820, w: 138, icon: "📤", t: "SignalR", s: "push to browsers", c: C.browser },
  ];
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">Reactive layering</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Pull where correctness needs backpressure; push where fan-out is cheap.</text>`;
  lanes.forEach((l, i) => {
    b += card(l.x, y, l.w, bh, { color: l.c, icon: l.icon, title: l.t, sub: l.s });
    if (i < lanes.length - 1) b += arrow(l.x + l.w, y + bh / 2, lanes[i + 1].x, y + bh / 2, { color: lanes[i + 1].c === C.muted ? "muted" : lanes[i + 1].c });
  });
  b += `<path d="M298 ${y + bh + 8} L298 ${y + bh + 22}" stroke="${C.pds}" stroke-width="1.5"/><text x="200" y="${y + bh + 40}" fill="${C.pds}" font-size="12">backpressure lives here (bounded channel)</text>`;
  b += `<path d="M889 ${y + bh + 8} L889 ${y + bh + 22}" stroke="${C.browser}" stroke-width="1.5"/><text x="720" y="${y + bh + 40}" fill="${C.browser}" font-size="12">pure fan-out (no backpressure)</text>`;
  return frame(w, h, b);
};

// 14. Statusphere anchor (scenarios).
D["statusphere"] = () => {
  const w = 900, h = 260, y = 108;
  let b = `<text x="30" y="60" fill="${C.text}" font-size="20" font-weight="700">The real-world anchor: Statusphere</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Our demo lexicon is essentially Bluesky's official teaching app — one emoji status per person, latest wins.</text>`;
  const nodes = [
    { x: 30, icon: "🧑", t: "pick an emoji", c: C.you },
    { x: 250, icon: "🏠", t: "record", s: "…status/self", c: C.pds },
    { x: 490, icon: "📡", t: "firehose", s: "→ relay", c: C.relay },
    { x: 710, icon: "📰", t: "board", s: "latest per user", c: C.appview },
  ];
  nodes.forEach((nd, i) => {
    b += card(nd.x, y, 180, 60, { color: nd.c, icon: nd.icon, title: nd.t, sub: nd.s });
    if (i < nodes.length - 1) b += arrow(nd.x + 180, y + 30, nodes[i + 1].x, y + 30, { color: nodes[i + 1].c });
  });
  b += `<text x="30" y="${y + 110}" fill="${C.muted}" font-size="12.5">Swap the lexicon (place.selfhost.status) for your own NSID and the same three services host a different app.</text>`;
  return frame(w, h, b);
};

// 15. Packaging / layering — the extractable libraries stacked, core never depends on a service.
D["packaging"] = () => {
  const w = 940, x0 = 76, x1 = 910, bh = 50, gap = 14, top = 100;
  const bands = [
    { role: "Hero app", sub: "not packaged", dashed: true, color: C.muted,
      pkgs: [["AtProto.Pds", C.pds], ["AtProto.Relay", C.relay], ["AtProto.AppView", C.appview], ["apps", C.muted], ["AppHost", C.muted]] },
    { role: "Aspire integration", color: C.appview, pkgs: [["AtProto.Hosting.Atproto", C.appview]] },
    { role: "Firehose / ingest", color: C.relay, pkgs: [["AtProto.Firehose", C.relay]] },
    { role: "Repository", color: C.pds, pkgs: [["AtProto.Repo", C.pds]] },
    { role: "Encodings + identity", color: C.pds, pkgs: [["AtProto.Car", C.pds], ["AtProto.Cbor", C.pds], ["AtProto.Identity", C.you]] },
    { role: "Primitives", color: C.you, pkgs: [["AtProto.Cid", C.you], ["AtProto.Crypto", C.you], ["AtProto.Lexicon", C.you], ["AtProto.OAuth", C.pds]] },
  ];
  const ys = bands.map((_, i) => top + i * (bh + gap));
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">Extractable libraries, layered</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Ten preview packages. Each layer depends only on the ones below it, and the core never references a service.</text>`;
  bands.forEach((band, i) => {
    const y = ys[i];
    b += `<g filter="url(#sh)"><rect x="${x0}" y="${y}" width="${x1 - x0}" height="${bh}" rx="12" fill="${C.panel}" stroke="${band.color}" stroke-width="1.5" ${band.dashed ? 'stroke-dasharray="6 4"' : ""}/><rect x="${x0}" y="${y}" width="6" height="${bh}" rx="3" fill="${band.color}"/></g>
    <text x="${x0 + 20}" y="${y + bh / 2 + (band.sub ? -3 : 5)}" fill="${C.text}" font-size="14" font-weight="650">${esc(band.role)}</text>`;
    if (band.sub) b += `<text x="${x0 + 20}" y="${y + bh / 2 + 14}" fill="${C.muted}" font-size="11">${esc(band.sub)}</text>`;
    let px = x0 + 250;
    for (const [t, c] of band.pkgs) { b += pill(px, y + bh / 2 - 11, t, c); px += t.length * 6.6 + 18 + 12; }
  });
  // One downward arrow in the left gutter = "depends on the layer below".
  b += arrow(50, ys[0] + 10, 50, ys[5] + bh - 6, { color: "muted" });
  const midY = (ys[0] + ys[5] + bh) / 2;
  b += `<text transform="rotate(-90 32 ${midY})" x="32" y="${midY}" text-anchor="middle" fill="${C.muted}" font-size="11">depends on ↓</text>`;
  const h = ys[5] + bh + 46;
  b += `<text x="30" y="${ys[5] + bh + 28}" fill="${C.muted}" font-size="12.5">Everything from the Aspire integration down ships as a preview package; the hero app on top consumes them and is never packaged.</text>`;
  return frame(w, h, b, "packaging / layering");
};

// 16. Federation — two PDS instances, one relay, one directory (they never talk directly).
D["federation"] = () => {
  const w = 940, h = 436;
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">Federation directory</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Two PDS instances announce to one relay; the AppView builds a directory. The instances never talk directly.</text>`;
  // Row 1: two independent PDS instances.
  b += card(70, 104, 250, 66, { color: C.pds, icon: "🏠", title: "PDS · Alpha", sub: "repos + place.selfhost.instance" });
  b += card(620, 104, 250, 66, { color: C.pds, icon: "🏠", title: "PDS · Beta", sub: "repos + place.selfhost.instance" });
  // Row 2: the shared relay.
  b += card(345, 214, 250, 66, { color: C.relay, icon: "📡", title: "Relay", sub: "aggregated firehose · listHosts" });
  // Row 3: the AppView directory view.
  b += card(300, 328, 340, 66, { color: C.appview, icon: "📇", title: "AppView · /directory", sub: "listHosts + instance records → directory" });
  // Announce + crawl edges (each PDS ↔ the one relay).
  b += arrow(240, 170, 400, 214, { color: "relay", label: "requestCrawl + crawl (ws)" });
  b += arrow(700, 170, 540, 214, { color: "relay", label: "requestCrawl + crawl (ws)" });
  // Relay → AppView (list the hosts it aggregates).
  b += arrow(470, 280, 470, 328, { color: "appview", label: "listHosts (http)" });
  // AppView reads each instance record straight from its PDS (outer edges, dashed).
  b += arrow(300, 352, 150, 170, { color: "pds", dash: true, label: "getRecord instance" });
  b += arrow(640, 352, 790, 170, { color: "pds", dash: true, label: "getRecord instance" });
  b += legend(30, 416, [["pds", C.pds], ["relay", C.relay], ["appview", C.appview]]);
  return frame(w, h, b, "federation / directory");
};

// 17. Extensibility map — where you plug in across the pipeline.
D["extensibility"] = () => {
  const w = 950, h = 384;
  const badge = (cx, cy, n, color = C.you) =>
    `<circle cx="${cx}" cy="${cy}" r="13" fill="${color}" stroke="${C.bg}" stroke-width="2.5"/><text x="${cx}" y="${cy + 4.5}" text-anchor="middle" font-size="13" font-weight="800" fill="${C.bg}">${n}</text>`;
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">Where you plug in</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">The stack is a pipeline. Each stage is an extension point, and two concerns cut across all of them.</text>`;
  const stages = [
    { icon: "📜", title: "Lexicon", sub: "define your record type", color: C.you },
    { icon: "🏠", title: "PDS / repo", sub: "new record or XRPC", color: C.pds },
    { icon: "📡", title: "Firehose", sub: "consume via library", color: C.relay },
    { icon: "📰", title: "AppView", sub: "your projection + store", color: C.appview },
    { icon: "🖥️", title: "UI + XRPC", sub: "your endpoints + board", color: C.browser },
  ];
  const cw = 150, gap = 40, x0 = 20, cy = 124, ch = 80;
  const xs = stages.map((_, i) => x0 + i * (cw + gap));
  const edge = [C.pds, C.relay, C.appview, C.browser];
  for (let i = 0; i < stages.length - 1; i++) {
    b += arrow(xs[i] + cw, cy + ch / 2, xs[i + 1], cy + ch / 2, { color: edge[i] });
  }
  stages.forEach((s, i) => {
    b += card(xs[i], cy, cw, ch, { color: s.color, icon: s.icon, title: s.title, sub: s.sub });
    b += badge(xs[i], cy, i + 1);
  });
  const bandX = x0, bandW = xs[4] + cw - x0;
  const band = (y, n, color, label, note) =>
    `<rect x="${bandX}" y="${y}" width="${bandW}" height="42" rx="12" fill="${C.panel2}" stroke="${color}" stroke-width="1.5" stroke-dasharray="6 4"/><rect x="${bandX}" y="${y}" width="6" height="42" rx="3" fill="${color}"/>` +
    badge(bandX + 26, y + 21, n, color) +
    `<text x="${bandX + 52}" y="${y + 20}" fill="${C.text}" font-size="14" font-weight="650">${esc(label)}</text>` +
    `<text x="${bandX + 52}" y="${y + 34}" fill="${C.muted}" font-size="12">${esc(note)}</text>`;
  b += band(232, 6, C.appview, "Aspire topology", "compose your own stack with AddAtproto* + With*");
  b += band(288, 7, C.pds, "Storage", "swap in-memory for durable persistence (follow ICursorStore)");
  b += legend(30, 356, [["lexicon", C.you], ["pds", C.pds], ["firehose", C.relay], ["appview", C.appview], ["ui", C.browser]]);
  return frame(w, h, b, "extensibility map");
};


// 18. Two AppViews over one firehose - same stream, different query shapes.
D["two-appviews"] = () => {
  const w = 980, h = 420;
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">Two AppViews over one firehose</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">One Relay stream can feed multiple projections, each with the store its query shape needs.</text>`;
  b += card(40, 180, 210, 70, { color: C.relay, icon: "📡", title: "Relay firehose", sub: "one ordered event stream" });
  b += pill(286, 200, "same #commit frames", C.relay);

  b += card(390, 108, 210, 70, { color: C.appview, icon: "📰", title: "Presence AppView", sub: "latest status per DID" });
  b += card(650, 108, 190, 70, { color: C.appview, icon: "🗂️", title: "in-memory KV", sub: "latest-wins point lookups" });
  b += card(650, 242, 190, 70, { color: C.you, icon: "🦆", title: "DuckDB", sub: "columnar OLAP aggregates" });
  b += card(390, 242, 210, 70, { color: C.appview, icon: "📊", title: "Analytics AppView", sub: "activity and repo stats" });
  b += card(870, 108, 88, 70, { color: C.browser, icon: "🖥️", title: "Board", sub: "presence", titleSize: 13 });
  b += card(870, 242, 88, 70, { color: C.browser, icon: "📈", title: "Stats", sub: "top repos", titleSize: 13 });

  b += arrow(250, 215, 390, 143, { color: "relay", label: "subscribe" });
  b += arrow(250, 215, 390, 277, { color: "relay", label: "subscribe" });
  b += arrow(600, 143, 650, 143, { color: "appview" });
  b += arrow(600, 277, 650, 277, { color: "appview" });
  b += arrow(840, 143, 870, 143, { color: "browser" });
  b += arrow(840, 277, 870, 277, { color: "browser" });

  b += `<text x="390" y="363" fill="${C.muted}" font-size="12.5">Same event stream, two materialized views: point-lookup KV for the board, aggregate OLAP for analytics.</text>`;
  b += legend(30, 394, [["relay / firehose", C.relay], ["appview", C.appview], ["analytics store", C.you], ["browser", C.browser]]);
  return frame(w, h, b, "projection fan-out");
};

// 19. Storage profiles - same code, one profile toggle.
D["storage-profiles"] = () => {
  const w = 980, h = 430;
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">Storage profiles</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">In-memory stays the default. Production opts into durable PDS stores and analytics with one env var.</text>`;

  const panel = (x, y, title, sub, color) =>
    `<g filter="url(#sh)"><rect x="${x}" y="${y}" width="430" height="286" rx="16" fill="${C.panel}" stroke="${color}" stroke-width="1.5"/></g>` +
    `<text x="${x + 22}" y="${y + 34}" fill="${C.text}" font-size="18" font-weight="700">${esc(title)}</text>` +
    `<text x="${x + 22}" y="${y + 55}" fill="${C.muted}" font-size="12.5">${esc(sub)}</text>`;

  b += panel(30, 104, "dev (default)", "zero config, nothing on disk", C.pds);
  b += panel(520, 104, "production", "ATPROTO_PROFILE=production", C.you);
  b += arrow(450, 246, 520, 246, { color: "you", label: "same code + env var" });

  b += card(58, 188, 170, 60, { color: C.pds, icon: "🏠", title: "PDS stores", sub: "accounts · repos · blobs" });
  b += card(260, 188, 150, 60, { color: C.pds, icon: "🧠", title: "memory", sub: "resets on restart" });
  b += card(146, 286, 178, 60, { color: C.appview, icon: "🖥️", title: "presence board", sub: "latest-wins only" });
  b += arrow(228, 218, 260, 218, { color: "pds" });
  b += arrow(335, 248, 250, 286, { color: "appview" });

  b += card(548, 178, 170, 60, { color: C.pds, icon: "🏠", title: "PDS stores", sub: "accounts · repos · blobs" });
  b += card(750, 178, 158, 60, { color: C.pds, icon: "🗄️", title: "SQLite", sub: "survives restart" });
  b += card(548, 282, 170, 60, { color: C.appview, icon: "🖥️", title: "presence board", sub: "latest-wins" });
  b += card(750, 282, 158, 60, { color: C.you, icon: "🦆", title: "DuckDB", sub: "analytics AppView" });
  b += arrow(718, 208, 750, 208, { color: "pds" });
  b += arrow(640, 238, 640, 282, { color: "appview" });
  b += arrow(718, 312, 750, 312, { color: "you" });

  b += `<text x="30" y="404" fill="${C.muted}" font-size="12.5">Default dev is disposable and fast; production keeps PDS data durable and adds the DuckDB read model for aggregate queries.</text>`;
  return frame(w, h, b, "dev vs production");
};

// 20. OAuth login flow (sequence): discover, push, consent, exchange, use.
D["oauth-flow"] = () =>
  sequence(900, "OAuth — a real client logs in and writes",
    [
      { name: "Client", color: C.appview, icon: "🔌" },
      { name: "PDS (AS + RS)", color: C.pds, icon: "🏠" },
      { name: "You (browser)", color: C.browser, icon: "🧑" },
    ],
    [
      { from: 0, to: 0, note: true, label: "resolve handle → DID → DID doc → PDS endpoint" },
      { from: 0, to: 1, label: "GET protected-resource + authorization-server metadata" },
      { from: 1, to: 0, label: "issuer · endpoints · PAR · S256 · ES256", color: C.pds },
      { from: 0, to: 0, note: true, label: "verify issuer == PDS origin" },
      { from: 0, to: 1, label: "POST /oauth/par (params + PKCE + DPoP)" },
      { from: 1, to: 0, label: "request_uri + DPoP-Nonce", color: C.pds },
      { from: 0, to: 2, label: "browser → /oauth/authorize?client_id & request_uri", color: C.browser },
      { from: 2, to: 1, label: "log in (password) + consent", color: C.browser },
      { from: 1, to: 2, label: "302 redirect_uri?code & state & iss", color: C.pds },
      { from: 0, to: 1, label: "POST /oauth/token (code + verifier + DPoP)" },
      { from: 1, to: 0, label: "access + refresh · sub=DID · scope", color: C.pds },
      { from: 0, to: 1, label: "XRPC write + access token + DPoP proof" },
      { from: 1, to: 0, label: "200 · DPoP-Nonce (or 400 use_dpop_nonce)", color: C.pds },
    ]);

// 21. OAuth trust chain: why the client believes this AS is really yours.
D["oauth-trust-chain"] = () => {
  const w = 980, h = 268;
  let b = `<text x="30" y="58" fill="${C.text}" font-size="21" font-weight="700">The trust chain</text>
  <text x="31" y="80" fill="${C.muted}" font-size="13">Identity flows from your DID, so the client can prove this authorization server is yours — no registry.</text>`;
  const steps = [
    { icon: "🧑", t: "handle", s: "you.example", c: C.you },
    { icon: "🪪", t: "DID", s: "did:plc:…", c: C.you },
    { icon: "📄", t: "DID document", s: "#atproto_pds endpoint", c: C.you },
    { icon: "🏠", t: "PDS (RS meta)", s: "authorization_servers", c: C.pds },
    { icon: "🔐", t: "AS metadata", s: "issuer == PDS origin", c: C.pds },
  ];
  const y = 120, bw = 172, bh = 66;
  const gap = (w - 48 - bw * steps.length) / (steps.length - 1);
  steps.forEach((st, i) => {
    const x = 24 + i * (bw + gap);
    b += card(x, y, bw, bh, { color: st.c, icon: st.icon, title: st.t, sub: st.s });
    if (i < steps.length - 1) b += arrow(x + bw, y + bh / 2, x + bw + gap, y + bh / 2, { color: steps[i + 1].c });
  });
  b += `<text x="30" y="${y + bh + 44}" fill="${C.muted}" font-size="12.5">The chain closes on itself: handle → DID → PDS → AS issuer, so a login can only succeed against the server your identity points at.</text>`;
  return frame(w, h, b, "DID-anchored trust");
};

// ---- emit ----------------------------------------------------------------------------
let count = 0;
for (const [name, build] of Object.entries(D)) {
  writeFileSync(join(OUT, `${name}.svg`), build().trim() + "\n");
  count++;
}
console.log(`wrote ${count} diagrams to docs/img/`);
