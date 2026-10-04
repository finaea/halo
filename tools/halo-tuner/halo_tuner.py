"""Halo tuner: a dev tool for adjusting each Azur Archive student's halo pose by eye.

Dev-only and standard library only. It is not in build.ps1, and nothing of it ships. Run it from
anywhere, with a Debug build in place for "Render exact":

    python tools\\halo-tuner\\halo_tuner.py [--port 8765] [--refs <folder>] [--no-browser]

It serves a page on http://localhost:<port> (the first free port from 8765 up; Windows reserves
some ranges) and opens it. Pick a student on the left; the sliders on the right set her halo's
pitch, roll, scale, dx, dy and depth.

- The live preview draws her halo from its JSON in the browser and poses it with the same 4x4
  matrix Halo uses (AmbientLoop.Pose), applied as a CSS matrix3d, over her face crop.
- "Render exact" writes the current values into the Debug build's copy
  (src\\Halo.Widgets\\bin\\Debug\\...\\game-art\\halos\\<id>.json) and runs the real harness
  (Halo.Widgets.exe --render) on her usual card, so what D2D draws is one click away. The repo is
  not touched.
- "Save" writes the pose and placement fields of
  assets\\skins\\azur-archive\\game-art\\halos\\<id>.json, never the shape parts, plus the Debug
  build's copy. An unchanged value keeps its exact spelling.

Reference portraits are optional: a folder of <name>-1.webp files given by --refs or the
HALO_TUNER_REFS environment variable. They are game art, so they are only read where they are and
never copied; without them the reference pane shows her face crop from game-art. Renders and the
harness's config go to a temporary folder under %TEMP%, removed on exit; nothing is written inside
the repo except the halo files on Save.
"""

import argparse
import atexit
import http.server
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import urllib.parse
import webbrowser
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ART = ROOT / "assets" / "skins" / "azur-archive" / "game-art"
HALOS = ART / "halos"
BIN = ROOT / "src" / "Halo.Widgets" / "bin" / "Debug" / "net10.0" / "win-x64"
BIN_HALOS = BIN / "assets" / "skins" / "azur-archive" / "game-art" / "halos"
EXE = BIN / "Halo.Widgets.exe"
CAST_CS = ROOT / "src" / "Halo.Widgets" / "Skins" / "AzurArchive" / "AzurCast.cs"

# the pose and placement fields, in the order they are written; nothing else is ever changed
POSE = ["pitch", "roll", "scale", "dx", "dy", "depth"]
DEFAULTS = {"pitch": 73.4, "roll": 0, "scale": 0.85, "dx": 0, "dy": -6, "depth": 4}

# the card each student sits on by default (AzurArchiveSkin's roster), and how to seat her there
USUAL = {
    "yuuka": ("cpu-ram", "character", "idle"), "midori": ("gpu", "character", "idle"),
    "momoi": ("fps", "character", "gaming"), "asuna": ("power", "character", "idle"),
    "akane": ("drives", "character", "idle"), "chihiro": ("network", "character", "idle"),
    "kotama": ("network", "character2", "idle"), "utaha": ("fans", "character", "idle"),
    "noa": ("topcpu", "character", "idle"), "koyuki": ("topram", "character", "idle"),
}
CARDS = ["cpu-ram", "gpu", "fps", "power", "drives", "network", "fans", "topcpu", "topram"]
PRESETS = ["port-day", "night-watch", "shittim", "momo-pink", "high-contrast"]
REF_NAME = {"aris": "alice"}

WORK = Path(tempfile.mkdtemp(prefix="halo-tuner-"))      # under %TEMP%, never the repo
atexit.register(shutil.rmtree, WORK, ignore_errors=True)


def colours():
    """The halo colours from AzurCast's Halos table, so the preview paints what Halo paints."""
    text = CAST_CS.read_text(encoding="utf-8")
    table = text[text.index("Halos = new()"):]
    table = table[:table.index("};")]
    return {k: "#" + v for k, v in re.findall(r'\["(\w+)"\]\s*=\s*Hex\(0x([0-9A-Fa-f]{6})\)', table)}


def read(id_):
    return json.loads((HALOS / f"{id_}.json").read_text(encoding="utf-8"))


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, separators=(", ", ": ")) + "\n", encoding="utf-8", newline="\n")


def merged(id_, pose):
    """Her file with only the pose fields replaced, pose fields first, the rest in file order."""
    data = read(id_)
    clean = {}
    for k in POSE:
        if k in pose and pose[k] is not None:
            v = round(float(pose[k]), 2)
            if k not in data and v == DEFAULTS[k]:
                continue    # a default the file never spelled out stays implicit
            if k in data and float(data[k]) == v:
                clean[k] = data[k]      # unchanged: keep it exactly as written
            else:
                clean[k] = int(v) if v == int(v) else v
    rest = {k: v for k, v in data.items() if k not in POSE}
    keep = {k: data[k] for k in POSE if k in data and k not in clean}
    return {**clean, **keep, **rest}


def render(id_, pose, card, preset):
    """Write her values into the Debug build's copy and draw her card with the real harness."""
    if not EXE.exists():
        raise RuntimeError(f"no Debug build at {EXE}: run dotnet build Halo.sln -c Debug first")
    write(BIN_HALOS / f"{id_}.json", merged(id_, pose))
    _, option, fixture = USUAL.get(id_, ("cpu-ram", "character", "idle"))
    if card != USUAL.get(id_, ("cpu-ram",))[0]:
        option, fixture = "character", "gaming" if card == "fps" else "idle"
    cfg = WORK / f"cfg-{id_}"
    cfg.mkdir(exist_ok=True)
    write(cfg / "settings.json", {"schemaVersion": 3, "appearance": {"skin": "azur-archive",
                                  "skins": {"azur-archive": {"preset": preset}}}})
    write(cfg / "widgets.json", {"schemaVersion": 3, "widgets": [{"id": "tuner", "type": card,
          "appearance": {"skins": {"azur-archive": {"options": {option: id_}}}}}]})
    out = WORK / f"{id_}-{card}-{preset}-{os.urandom(3).hex()}.png"
    env = dict(os.environ, DOTNET_ROOT=os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\dotnet"))
    r = subprocess.run([str(EXE), "--render", str(out), "--config", str(cfg), "--widget", "tuner",
                        "--scale", "5.1", "--fixture", fixture, "--warp"],
                       env=env, capture_output=True, text=True, timeout=120)
    if r.returncode != 0 or not out.exists():
        raise RuntimeError(f"harness failed ({r.returncode}): {(r.stderr or r.stdout).strip()[:400]}")
    return out.name


class Handler(http.server.BaseHTTPRequestHandler):
    refs: Path | None = None

    def log_message(self, fmt, *args):
        pass

    def send(self, code, body, kind="application/json"):
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
        self.send_response(code)
        self.send_header("Content-Type", kind)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def file(self, path, kind):
        if path.exists() and path.is_file():
            self.send(200, path.read_bytes(), kind)
        else:
            self.send(404, {"error": f"not found: {path.name}"})

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        name = urllib.parse.unquote(url.path.rsplit("/", 1)[-1])
        if url.path == "/":
            self.send(200, PAGE.encode(), "text/html; charset=utf-8")
        elif url.path == "/api/cast":
            hues = colours()
            cast = []
            for f in sorted(HALOS.glob("*.json")):
                id_ = f.stem
                data = read(id_)
                cast.append({"id": id_, "colour": hues.get(id_, "#3DD2F9"), "data": data,
                             "card": USUAL.get(id_, ("cpu-ram",))[0]})
            self.send(200, {"cast": cast, "cards": CARDS, "presets": PRESETS, "defaults": DEFAULTS,
                            "bin": EXE.exists()})
        elif url.path.startswith("/face/") and re.fullmatch(r"[a-z]+", name):
            self.file(ART / "blue-archive" / f"{name}.png", "image/png")
        elif url.path.startswith("/ref/") and re.fullmatch(r"[a-z]+", name):
            webp = self.refs / f"{REF_NAME.get(name, name)}-1.webp" if self.refs else None
            if webp and webp.exists():
                self.file(webp, "image/webp")
            else:
                self.file(ART / "blue-archive" / f"{name}.png", "image/png")
        elif url.path.startswith("/render/") and re.fullmatch(r"[\w.-]+\.png", name):
            self.file(WORK / name, "image/png")
        else:
            self.send(404, {"error": "not found"})

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
        id_ = body.get("id", "")
        if not re.fullmatch(r"[a-z]+", id_) or not (HALOS / f"{id_}.json").exists():
            return self.send(400, {"error": f"unknown id '{id_}'"})
        try:
            if self.path == "/api/render":
                card = body.get("card") if body.get("card") in CARDS else USUAL.get(id_, ("cpu-ram",))[0]
                preset = body.get("preset") if body.get("preset") in PRESETS else "port-day"
                self.send(200, {"png": "/render/" + render(id_, body.get("pose", {}), card, preset)})
            elif self.path == "/api/save":
                data = merged(id_, body.get("pose", {}))
                write(HALOS / f"{id_}.json", data)
                if BIN_HALOS.exists():
                    write(BIN_HALOS / f"{id_}.json", data)
                self.send(200, {"saved": data})
            else:
                self.send(404, {"error": "not found"})
        except Exception as ex:  # a tuning tool: show the reason in the page rather than dying
            self.send(500, {"error": f"{type(ex).__name__}: {ex}"})


PAGE = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>Halo tuner</title>
<style>
  :root { --bg:#14161b; --panel:#1d2027; --ink:#e7e9ee; --muted:#9097a6; --line:#2c313b; --accent:#6fb3ff; --warn:#ffb454; --ok:#7ee08f; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.4 system-ui, sans-serif; display:grid;
         grid-template-columns:170px 1fr 330px; height:100vh; }
  #list { border-right:1px solid var(--line); overflow:auto; padding:8px 0; }
  #list button { display:flex; justify-content:space-between; width:100%; background:none; border:0; color:var(--ink);
                 padding:6px 14px; text-align:left; cursor:pointer; font:inherit; }
  #list button.on { background:var(--panel); color:var(--accent); }
  #list .dot { width:9px; height:9px; border-radius:50%; margin-top:5px; }
  #list .dirty::after { content:"•"; color:var(--warn); margin-left:6px; }
  #stage { overflow:auto; padding:16px; display:flex; flex-direction:column; gap:14px; }
  .row { display:flex; gap:14px; flex-wrap:wrap; align-items:flex-start; }
  .card { position:relative; width:300px; height:330px; border-radius:10px; overflow:hidden; }
  .card.light { background:#EFF5FB; } .card.dark { background:#171D2C; }
  .card .ceil { position:absolute; left:0; right:0; border-top:1px dashed #e05a5a; }
  .card .ceil span { position:absolute; right:6px; top:-15px; font-size:11px; color:#e05a5a; }
  .card img.face { position:absolute; -webkit-mask-image:radial-gradient(circle closest-side, #000 84%, transparent 100%);
                   mask-image:radial-gradient(circle closest-side, #000 84%, transparent 100%); }
  .card canvas { position:absolute; left:0; top:0; transform-origin:0 0; }
  .label { font-size:12px; color:var(--muted); margin-bottom:4px; }
  .ref { width:300px; height:330px; overflow:hidden; border-radius:10px; background:#5a5a5a; display:flex; align-items:center; justify-content:center; }
  .ref canvas { max-width:100%; max-height:100%; }
  #exact { max-width:620px; border-radius:10px; background:#000; }
  #side { border-left:1px solid var(--line); padding:14px; overflow:auto; }
  h2 { margin:0 0 4px; font-size:18px; }
  .state { font-size:12px; margin-bottom:12px; }
  .state.saved { color:var(--ok); } .state.dirty { color:var(--warn); }
  .param { margin:10px 0 14px; }
  .param .head { display:flex; justify-content:space-between; align-items:center; }
  .param input[type=range] { width:100%; }
  .param input[type=number] { width:76px; background:var(--panel); color:var(--ink); border:1px solid var(--line); border-radius:4px; padding:2px 4px; }
  .param small { color:var(--muted); display:block; }
  .param button { background:none; border:1px solid var(--line); color:var(--muted); border-radius:4px; cursor:pointer; font-size:11px; padding:1px 6px; }
  .actions { display:flex; gap:8px; flex-wrap:wrap; margin:16px 0; }
  .actions button { background:var(--panel); color:var(--ink); border:1px solid var(--line); border-radius:6px; padding:7px 12px; cursor:pointer; font:inherit; }
  .actions button.primary { background:var(--accent); color:#0b1320; border-color:var(--accent); font-weight:600; }
  select { background:var(--panel); color:var(--ink); border:1px solid var(--line); border-radius:4px; padding:3px; }
  #msg { font-size:12px; color:var(--muted); min-height:18px; white-space:pre-wrap; }
  .keys { font-size:12px; color:var(--muted); margin-top:18px; }
</style></head>
<body>
<nav id="list"></nav>
<main id="stage">
  <div class="row">
    <div><div class="label">Light card (Port Day)</div><div class="card light" id="cardL"></div></div>
    <div><div class="label">Dark card (Night Watch)</div><div class="card dark" id="cardD"></div></div>
    <div><div class="label">In-game reference</div><div class="ref" id="refBox"></div></div>
  </div>
  <div><div class="label" id="exactLabel">Render exact: the real harness (D2D), her card at 3×</div><img id="exact" alt=""></div>
</main>
<aside id="side">
  <h2 id="name"></h2>
  <div class="state" id="state"></div>
  <div id="params"></div>
  <label><input type="checkbox" id="turn"> Turn it (preview of the spin)</label>
  <div class="actions">
    <button class="primary" id="save">Save</button>
    <button id="render">Render exact</button>
    <button id="revert">Reset all to saved</button>
  </div>
  <div>Card <select id="cardSel"></select> &nbsp; Preset <select id="presetSel"></select></div>
  <div id="msg"></div>
  <div class="keys">Keys (outside a box): <b>n</b> / <b>p</b> next / previous student, <b>s</b> save, <b>r</b> render exact.</div>
</aside>
<script>
"use strict";
// ---- one-to-one with Halo: HaloShape parts, AmbientLoop.Pose, AzurCast.DrawHalo ----
const ZOOM = 4;           // preview px per design px: a 48 px face is 192 px here
const REACH = 1.12, HALO_DIP = 9, FLATTEST = 76;
const HEADROOM = 19;      // the CPU card's seat: its ceiling is about 19 px above the face
const SPEC = {
  pitch: {min: 0, max: 85, step: 0.5, help: "degrees it leans back; 0 faces you, higher is flatter"},
  roll:  {min: -45, max: 45, step: 0.5, help: "degrees it leans sideways, clockwise"},
  scale: {min: 0.3, max: 1.6, step: 0.01, help: "radius as a share of half the face"},
  dx:    {min: -24, max: 24, step: 0.5, help: "centre left / right of the face's middle (px of a 48 px face)"},
  dy:    {min: -24, max: 12, step: 0.5, help: "centre above (−) or below the face's top edge (px)"},
  depth: {min: 0, max: 12, step: 0.1, help: "perspective in radii: smaller = stronger, 0 = none"},
};
let cast = [], defaults = {}, cur = null, pose = {}, saved = {}, spin = 0, dirty = new Set();

function onCircle(cx, cy, r, deg) { const a = deg * Math.PI / 180; return [cx + Math.sin(a) * r, cy - Math.cos(a) * r]; }
function hexRgb(h) { return [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16) / 255); }
function shade(rgb, tone, alpha) {
  const k = Math.abs(tone || 0), to = (tone || 0) > 0 ? 1 : 0;
  const c = rgb.map(v => Math.round((v + (to - v) * k) * 255));
  return `rgba(${c[0]},${c[1]},${c[2]},${0.9 * (alpha ?? 1)})`;
}
const KINDS = ["ring", "arc", "ellipse", "poly", "star", "dot"];

// HaloShape.Draw, flat-on: unit plane scaled by r about (0,0), strokes w × r
function drawFlat(ctx, parts, rgb, r) {
  ctx.lineJoin = "round"; ctx.lineCap = "butt"; ctx.miterLimit = 2;
  for (const p of parts) {
    const kind = KINDS.find(k => k in p), a = p[kind];
    const at = p.at || [0, 0], fill = kind === "dot" || !!p.fill;
    ctx.beginPath();
    if (kind === "ring") ctx.arc(at[0] * r, at[1] * r, a[0] * r, 0, 2 * Math.PI);
    else if (kind === "dot") ctx.arc(a[0] * r, a[1] * r, a[2] * r, 0, 2 * Math.PI);
    else if (kind === "ellipse") ctx.ellipse(a[0] * r, a[1] * r, a[2] * r, a[3] * r, 0, 0, 2 * Math.PI);
    else if (kind === "arc") {
      let from = a[1], to = a[2]; while (to <= from) to += 360;
      ctx.arc(at[0] * r, at[1] * r, a[0] * r, (from - 90) * Math.PI / 180, (to - 90) * Math.PI / 180, false);
    } else if (kind === "poly") {
      ctx.moveTo(a[0] * r, a[1] * r);
      for (let i = 2; i + 1 < a.length; i += 2) ctx.lineTo(a[i] * r, a[i + 1] * r);
      if (p.closed !== false) ctx.closePath();
    } else if (kind === "star") {
      const n = Math.max(2, Math.floor(a[3])), inner = p.inner ?? 0.4;
      let [x, y] = onCircle(a[0], a[1], a[2], 0); ctx.moveTo(x * r, y * r);
      for (let i = 1; i < n * 2; i++) { [x, y] = onCircle(a[0], a[1], i % 2 === 0 ? a[2] : a[2] * inner, i * 180 / n); ctx.lineTo(x * r, y * r); }
      ctx.closePath();
    }
    const col = shade(rgb, p.tone, p.alpha);
    if (fill) { ctx.fillStyle = col; ctx.fill(); } else { ctx.strokeStyle = col; ctx.lineWidth = (p.w ?? 0.12) * r; ctx.stroke(); }
  }
}

// AmbientLoop.Pose: row vectors, RotX(pitch)·RotZ(roll)·flatten (M34 = −1/(depth·r)). Halo also
// zeroes M33 (the picture lands on z = 0); CSS refuses to draw a non-invertible matrix, so the page
// keeps z. x, y and w — the picture — are identical either way.
function mul(a, b) { const m = new Array(16).fill(0); for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) for (let k = 0; k < 4; k++) m[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j]; return m; }
function rotX(t) { const c = Math.cos(t), s = Math.sin(t); return [1,0,0,0, 0,c,s,0, 0,-s,c,0, 0,0,0,1]; }
function rotZ(t) { const c = Math.cos(t), s = Math.sin(t); return [c,s,0,0, -s,c,0,0, 0,0,1,0, 0,0,0,1]; }
function poseM(pitchDeg, rollDeg, depth, r) {
  const flat = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  if (depth > 0) flat[11] = -1 / (depth * r);
  return mul(mul(rotX(pitchDeg * Math.PI / 180), rotZ(rollDeg * Math.PI / 180)), flat);
}
function project(m, x, y) { const X = x * m[0] + y * m[4] + m[12], Y = x * m[1] + y * m[5] + m[13], W = x * m[3] + y * m[7] + m[15]; return [X / W, Y / W]; }
function posedBounds(p, r, pitch) {
  const m = poseM(pitch, p.roll, p.depth, r); let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
  for (let i = 0; i < 48; i++) { const a = i * Math.PI / 24, [x, y] = project(m, Math.cos(a) * r * REACH, Math.sin(a) * r * REACH);
    x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
  return {left: x0, top: y0, width: x1 - x0, height: y1 - y0};
}

// AzurCast.DrawHalo, in design px with the face slot at (0,0)–(48,48): place, then keep under the ceiling
function place(p) {
  let r = 24 * p.scale, cx = 24 + p.dx, cy = p.dy, pitch = p.pitch, ceiling = -HEADROOM;
  let box = posedBounds(p, r, pitch);
  const room = Math.max(4, HALO_DIP - ceiling);
  while (box.height > room && pitch < FLATTEST) { pitch = Math.min(FLATTEST, pitch + 2); box = posedBounds(p, r, pitch); }
  if (box.height > room) { r *= room / box.height; box = posedBounds(p, r, pitch); }
  if (cy + box.top < ceiling) cy = ceiling - box.top;
  return {r, cx, cy, pitch, flattened: pitch > p.pitch, shrunk: r < 24 * p.scale - 1e-6};
}

function full() { const p = {...defaults}; for (const k of Object.keys(SPEC)) if (pose[k] !== undefined) p[k] = Number(pose[k]); return p; }

function drawCard(el) {
  const p = full(), pl = place(p), rgb = hexRgb(cur.colour);
  const slot = {x: 150 - 24 * ZOOM, y: 330 - 48 * ZOOM - 14};  // face low in the box, room above for the halo
  el.innerHTML = "";
  const ceil = document.createElement("div"); ceil.className = "ceil"; ceil.style.top = (slot.y - HEADROOM * ZOOM) + "px";
  ceil.innerHTML = "<span>header rule (CPU card seat)</span>"; el.appendChild(ceil);
  const img = document.createElement("img"); img.className = "face"; img.src = "/face/" + cur.id;
  Object.assign(img.style, {left: slot.x + "px", top: slot.y + "px", width: 48 * ZOOM + "px", height: 48 * ZOOM + "px"}); el.appendChild(img);
  // flat-on canvas, then the same pose as Halo, through CSS matrix3d (row-major = CSS column-major)
  const R = pl.r * ZOOM, S = Math.ceil(2 * R * REACH) + 4, dpr = window.devicePixelRatio || 1;
  const cv = document.createElement("canvas"); cv.width = S * dpr * 2; cv.height = S * dpr * 2; cv.style.width = S + "px"; cv.style.height = S + "px";
  const ctx = cv.getContext("2d"); ctx.scale(dpr * 2, dpr * 2); ctx.translate(S / 2, S / 2); drawFlat(ctx, cur.data.parts, rgb, R);
  let m = poseM(pl.pitch, p.roll, p.depth, R);
  if (spin) m = mul(rotZ(spin), m);
  cv.style.transform = `translate(${slot.x + pl.cx * ZOOM}px, ${slot.y + pl.cy * ZOOM}px) matrix3d(${m.join(",")}) translate(${-S / 2}px, ${-S / 2}px)`;
  el.appendChild(cv);
  return pl;
}

function redraw() {
  const pl = drawCard(document.getElementById("cardL")); drawCard(document.getElementById("cardD"));
  const isDirty = Object.keys(SPEC).some(k => Number(pose[k] ?? defaults[k]) !== Number(saved[k] ?? defaults[k]));
  isDirty ? dirty.add(cur.id) : dirty.delete(cur.id);
  const st = document.getElementById("state");
  st.className = "state " + (isDirty ? "dirty" : "saved");
  st.textContent = (isDirty ? "Unsaved changes" : "Saved — matches the file") +
    (pl.flattened ? `  ·  on this seat it leans to ${pl.pitch}° to clear the header` : "") + (pl.shrunk ? "  ·  and shrinks" : "");
  for (const b of document.querySelectorAll("#list button")) b.classList.toggle("dirty", dirty.has(b.dataset.id));
}

function buildParams() {
  const box = document.getElementById("params"); box.innerHTML = "";
  for (const [k, s] of Object.entries(SPEC)) {
    const v = pose[k] ?? defaults[k];
    const d = document.createElement("div"); d.className = "param";
    d.innerHTML = `<div class="head"><b>${k}</b><span><input type="number" step="${s.step}" value="${v}"> <button title="back to the saved value">reset</button></span></div>
      <input type="range" min="${s.min}" max="${s.max}" step="${s.step}" value="${v}"><small>${s.help}</small>`;
    const num = d.querySelector("input[type=number]"), rng = d.querySelector("input[type=range]");
    const set = val => { pose[k] = Number(val); num.value = pose[k]; rng.value = pose[k]; redraw(); };
    rng.oninput = () => set(rng.value); num.oninput = () => { if (num.value !== "") set(num.value); };
    d.querySelector("button").onclick = () => set(saved[k] ?? defaults[k]);
    box.appendChild(d);
  }
}

function select(id) {
  cur = cast.find(c => c.id === id);
  saved = {}; for (const k of Object.keys(SPEC)) if (k in cur.data) saved[k] = cur.data[k];
  pose = {...saved};
  document.getElementById("name").textContent = id[0].toUpperCase() + id.slice(1);
  showRef(id);
  document.getElementById("cardSel").value = cur.card;
  document.getElementById("exact").removeAttribute("src");
  document.getElementById("exactLabel").textContent = "Render exact: the real harness (D2D), her card at 3×";
  for (const b of document.querySelectorAll("#list button")) b.classList.toggle("on", b.dataset.id === id);
  buildParams(); redraw(); msg("");
}

// the reference portrait, cropped to the top of her figure where the halo floats
function showRef(id) {
  const box = document.getElementById("refBox"); box.innerHTML = "";
  const img = new Image();
  img.onload = () => {
    const c = document.createElement("canvas"); c.width = img.width; c.height = img.height;
    const g = c.getContext("2d"); g.drawImage(img, 0, 0);
    const a = g.getImageData(0, 0, c.width, c.height).data;
    let x0 = c.width, y0 = c.height, x1 = 0, y1 = 0;
    for (let y = 0; y < c.height; y += 2) for (let x = 0; x < c.width; x += 2)
      if (a[(y * c.width + x) * 4 + 3] > 16) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
    const tall = (y1 - y0) > 1.3 * (x1 - x0);           // a full figure: keep its top fifth
    const h = tall ? Math.round((y1 - y0) * 0.22) : (y1 - y0), w = x1 - x0;
    const out = document.createElement("canvas"); out.width = w; out.height = h;
    out.getContext("2d").drawImage(c, x0, y0, w, h, 0, 0, w, h);
    box.appendChild(out);
  };
  img.src = "/ref/" + id;
}

function msg(t) { document.getElementById("msg").textContent = t; }

async function post(url, body) {
  const r = await fetch(url, {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(body)});
  const j = await r.json(); if (!r.ok) throw new Error(j.error || r.statusText); return j;
}

async function save() {
  try {
    const j = await post("/api/save", {id: cur.id, pose: full()});
    cur.data = j.saved; saved = {}; for (const k of Object.keys(SPEC)) if (k in j.saved) saved[k] = j.saved[k];
    pose = {...saved}; redraw(); msg("Saved " + cur.id + ".json (repo and Debug build).");
  } catch (e) { msg("Save failed: " + e.message); }
}

async function renderExact() {
  const card = document.getElementById("cardSel").value, preset = document.getElementById("presetSel").value;
  msg("Rendering " + cur.id + " on " + card + " / " + preset + " with the harness…");
  try {
    const j = await post("/api/render", {id: cur.id, pose: full(), card, preset});
    document.getElementById("exact").src = j.png;
    document.getElementById("exactLabel").textContent = `Render exact: ${cur.id} on ${card}, ${preset}, these values (unsaved ones too)`;
    msg("Rendered. The Debug build's copy now holds these values; Save makes them permanent.");
  } catch (e) { msg("Render failed: " + e.message); }
}

function step(d) { const i = cast.findIndex(c => c.id === cur.id); select(cast[(i + d + cast.length) % cast.length].id); }

(async () => {
  const j = await (await fetch("/api/cast")).json();
  cast = j.cast; defaults = j.defaults;
  const list = document.getElementById("list");
  for (const c of cast) {
    const b = document.createElement("button"); b.dataset.id = c.id;
    b.innerHTML = `<span>${c.id}</span><span class="dot" style="background:${c.colour}"></span>`;
    b.onclick = () => select(c.id); list.appendChild(b);
  }
  for (const c of j.cards) document.getElementById("cardSel").add(new Option(c, c));
  for (const p of j.presets) document.getElementById("presetSel").add(new Option(p, p));
  document.getElementById("save").onclick = save;
  document.getElementById("render").onclick = renderExact;
  document.getElementById("revert").onclick = () => { pose = {...saved}; buildParams(); redraw(); };
  if (!j.bin) msg("No Debug build found: Render exact needs `dotnet build Halo.sln -c Debug` first.");
  document.addEventListener("keydown", e => {
    if (e.target.tagName === "INPUT" || e.target.tagName === "SELECT") return;
    if (e.key === "n") step(1); else if (e.key === "p") step(-1);
    else if (e.key === "s") save(); else if (e.key === "r") renderExact();
  });
  let last = performance.now();
  (function tick(now) {
    if (document.getElementById("turn").checked && cur) { spin = (spin + (now - last) / 1000 * 2 * Math.PI / 12) % (2 * Math.PI); redraw(); }
    else if (spin) { spin = 0; if (cur) redraw(); }
    last = now; requestAnimationFrame(tick);
  })(performance.now());
  select(cast[0].id);
})();
</script></body></html>
"""


def main():
    ap = argparse.ArgumentParser(description="Tune each Azur Archive halo's pose by eye.")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--refs", type=Path, default=os.environ.get("HALO_TUNER_REFS") or None,
                    help="optional folder of <name>-1.webp reference portraits (or HALO_TUNER_REFS)")
    ap.add_argument("--no-browser", action="store_true")
    args = ap.parse_args()
    if not HALOS.exists():
        sys.exit(f"no halo files at {HALOS}")
    Handler.refs = Path(args.refs) if args.refs else None
    # Windows reserves port ranges (Hyper-V, WinNAT) and refuses them with 10013: walk up from the
    # asked-for port so the address stays the same run to run, and only then let the OS pick
    server = None
    for port in [*range(args.port, args.port + 35), 0]:
        try:
            server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
            break
        except OSError:
            continue
    if server is None:
        sys.exit("no free local port")
    url = f"http://localhost:{server.server_address[1]}"
    print(f"Halo tuner on {url}  (Ctrl+C to stop; renders go to {WORK})")
    if not args.no_browser:
        threading.Timer(0.5, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
