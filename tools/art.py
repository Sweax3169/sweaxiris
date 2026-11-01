# Generates the README / site artwork for SweaxIris as animated SVGs (dark + light, en + tr).
# The iris is the same geometry as UI/Iris.cs; IBM Plex Sans is embedded as a small subset.
# usage: python tools/art.py docs/art
import base64, io, math, os, sys

from fontTools import subset
from fontTools.ttLib import TTFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONTS = os.path.join(ROOT, 'src', 'SweaxIris', 'Assets', 'Fonts')

T = {
    'dark': dict(bg1='#13171D', bg2='#090B0E', panel='#161A20', raised='#1D2229', edge='rgba(255,255,255,.10)', ink='#E9ECF1',
                 ink2='rgba(233,236,241,.72)', ink3='rgba(233,236,241,.45)', line='rgba(255,255,255,.10)', accent='#2BD4B4',
                 tally='#FF3D5E', blade='#232830', bladeEdge='#3F4753', scale='#606976', rim='#333A45', meter='#3DD68C', mid='#F2C94C',
                 shadow='.55', glow='.20', cap='#1D2229', capEdge='#3A424E'),
    'light': dict(bg1='#F5F7FA', bg2='#E3E7EC', panel='#FFFFFF', raised='#FFFFFF', edge='rgba(18,21,26,.10)', ink='#12151A',
                  ink2='rgba(18,21,26,.72)', ink3='rgba(18,21,26,.48)', line='rgba(18,21,26,.10)', accent='#0B937C',
                  tally='#E31F43', blade='#2A2F37', bladeEdge='#4A525E', scale='#878F9B', rim='#BDC4CE', meter='#1EA863', mid='#C99610',
                  shadow='.18', glow='.14', cap='#FFFFFF', capEdge='#BDC4CE'),
}

L = {
    'en': dict(kicker='Screen recorder for Windows 10 and 11', tag='Opens like a lens.',
               sub1='Record a screen, a window or a region you draw, with system sound', sub2='and your microphone. Pause and carry on in the same file.',
               keys='record', region='region', facts='NVENC · AMF · Quick Sync · x264   ·   up to 240 fps   ·   MP4 · MKV   ·   offline, free',
               rec='REC', paused='PAUSED'),
    'tr': dict(kicker='Windows 10 ve 11 için ekran kaydedici', tag='Lens gibi açılır.',
               sub1='Bir ekranı, bir pencereyi ya da çizdiğin bölgeyi; sistem sesi ve', sub2='mikrofonla kaydet. Duraklat, aynı dosyada devam et.',
               keys='kayıt', region='bölge', facts='NVENC · AMF · Quick Sync · x264   ·   240 fps’e kadar   ·   MP4 · MKV   ·   çevrimdışı, ücretsiz',
               rec='KAYIT', paused='DURAKLADI'),
}

# ── fonts ───────────────────────────────────────────────────────────────────
_font_cache = {}


def font_face(family, file, text):
    key = (file, text)
    if key not in _font_cache:
        f = TTFont(os.path.join(FONTS, file))
        opts = subset.Options()
        opts.flavor = 'woff2'
        opts.layout_features = ['kern', 'liga']
        sub = subset.Subsetter(opts)
        sub.populate(text=text)
        sub.subset(f)
        buf = io.BytesIO()
        f.flavor = 'woff2'
        f.save(buf)
        _font_cache[key] = base64.b64encode(buf.getvalue()).decode()
    return f"@font-face{{font-family:'{family}';src:url(data:font/woff2;base64,{_font_cache[key]}) format('woff2')}}"


def style(text):
    chars = ''.join(sorted(set(text + '0123456789:·×—–→ ')))
    return ('<style>' + font_face('IrisSans', 'IBMPlexSans-Regular.ttf', chars) + font_face('IrisSemi', 'IBMPlexSans-SemiBold.ttf', chars)
            + font_face('IrisMed', 'IBMPlexSans-Medium.ttf', chars)
            + "text{font-family:'IrisSans',sans-serif}.semi{font-family:'IrisSemi',sans-serif}.med{font-family:'IrisMed',sans-serif}</style>")


# ── the iris ────────────────────────────────────────────────────────────────

def iris_paths(cx, cy, R, ap, turn=0, n=7, scale=True):
    """Blade paths, hole polygon and radii for one aperture (same maths as UI/Iris.cs)."""
    rb = R * 0.80 if scale else R * 0.96
    r = rb * (0.06 + 0.86 * ap)
    theta = math.radians(turn + (1 - ap) * 58) - math.pi / 2
    v = [(cx + math.cos(theta + i * 2 * math.pi / n) * r, cy + math.sin(theta + i * 2 * math.pi / n) * r) for i in range(n)]
    e = []
    rr = rb * 1.02
    for j in range(n):
        fx, fy = v[(j - 1) % n]
        dx, dy = v[j][0] - fx, v[j][1] - fy
        ln = math.hypot(dx, dy) or 1
        dx, dy = dx / ln, dy / ln
        px, py = v[j][0] - cx, v[j][1] - cy
        b = 2 * (px * dx + py * dy)
        c = px * px + py * py - rr * rr
        t = (-b + math.sqrt(max(0, b * b - 4 * c))) / 2
        e.append((v[j][0] + dx * t, v[j][1] + dy * t))
    blades = []
    for i in range(n):
        k = (i + 1) % n
        blades.append(f"M{v[i][0]:.1f},{v[i][1]:.1f} L{v[k][0]:.1f},{v[k][1]:.1f} L{e[k][0]:.1f},{e[k][1]:.1f} A{rr:.1f},{rr:.1f} 0 0,0 {e[i][0]:.1f},{e[i][1]:.1f} Z")
    hole = 'M' + ' L'.join(f'{x:.1f},{y:.1f}' for x, y in v) + ' Z'
    return blades, hole, rb, r


def iris(cx, cy, R, c, keyframes, dur=8.0, uid='i', scale=True, cores=None, n=7):
    """An animated iris: keyframes is a list of (time 0..1, aperture, turn)."""
    frames = [iris_paths(cx, cy, R, ap, turn, n, scale) for _, ap, turn in keyframes]
    times = ';'.join(f'{t:.3f}' for t, _, _ in keyframes)
    spline = ';'.join(['.45 0 .2 1'] * (len(keyframes) - 1))
    anim = lambda vals: f'<animate attributeName="d" dur="{dur}s" repeatCount="indefinite" calcMode="spline" keyTimes="{times}" keySplines="{spline}" values="{vals}"/>'
    rb = frames[0][2]
    out = []
    if scale:
        out.append(f'<circle cx="{cx}" cy="{cy}" r="{R*0.995:.1f}" fill="none" stroke="{c["rim"]}" stroke-width="{max(1, R/90):.1f}"/>')
        for i in range(72):
            a = math.radians(i * 5)
            major = i % 6 == 0
            r0, r1 = (R * 0.86 if major else R * 0.895), R * 0.95
            out.append(f'<line x1="{cx+math.cos(a)*r0:.1f}" y1="{cy+math.sin(a)*r0:.1f}" x2="{cx+math.cos(a)*r1:.1f}" y2="{cy+math.sin(a)*r1:.1f}" stroke="{c["scale"]}" stroke-width="{max(1, R/(80 if major else 110)):.1f}"/>')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="{rb:.1f}" fill="{c["blade"]}" stroke="{c["rim"]}" stroke-width="{max(1,R/70):.1f}"/>')
    out.append(f'<clipPath id="{uid}c"><circle cx="{cx}" cy="{cy}" r="{rb:.1f}"/></clipPath>')
    # light through the lens: a gradient whose colour can change with the state
    core = cores or [(0, c['accent'])]
    grad = f'<radialGradient id="{uid}g" cx=".45" cy=".4" r=".65"><stop offset="0" stop-color="#FFFFFF" stop-opacity=".55"/><stop offset=".55" stop-color="{core[0][1]}" stop-opacity="0"/></radialGradient>'
    out.append(f'<defs>{grad}</defs>')
    col_anim = ''
    if len(core) > 1:
        col_anim = f'<animate attributeName="fill" dur="{dur}s" repeatCount="indefinite" calcMode="discrete" keyTimes="{";".join(f"{t:.3f}" for t, _ in core)}" values="{";".join(col for _, col in core)}"/>'
    out.append(f'<g clip-path="url(#{uid}c)">')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="{rb:.1f}" fill="{core[0][1]}">{col_anim}</circle>')
    out.append(f'<circle cx="{cx}" cy="{cy}" r="{rb:.1f}" fill="url(#{uid}g)"/>')
    for i in range(len(frames[0][0])):
        mid = math.atan2(*reversed([0, 0]))
        lit = 0.5 + 0.5 * math.cos(i * 2 * math.pi / len(frames[0][0]) + 0.8)
        shade = c['bladeEdge'] if lit > 0.62 else c['blade']
        vals = ';'.join(f[0][i] for f in frames)
        out.append(f'<path d="{frames[0][0][i]}" fill="{shade}" fill-opacity="{0.85 + 0.15 * lit:.2f}" stroke="{c["bladeEdge"]}" stroke-width="{max(.8, R/220):.2f}">{anim(vals)}</path>')
    out.append('</g>')
    return '\n'.join(out)


def defs(c, extra=''):
    return f'''<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{c['bg1']}"/><stop offset="1" stop-color="{c['bg2']}"/></linearGradient>
  <radialGradient id="ga"><stop offset="0" stop-color="{c['accent']}" stop-opacity="{c['glow']}"/><stop offset="1" stop-color="{c['accent']}" stop-opacity="0"/></radialGradient>
  <radialGradient id="gt"><stop offset="0" stop-color="{c['tally']}" stop-opacity="{c['glow']}"/><stop offset="1" stop-color="{c['tally']}" stop-opacity="0"/></radialGradient>
  <filter id="sh" x="-30%" y="-30%" width="160%" height="170%"><feGaussianBlur in="SourceAlpha" stdDeviation="12"/><feOffset dy="10"/><feComponentTransfer><feFuncA type="linear" slope="{c['shadow']}"/></feComponentTransfer><feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge></filter>
  {extra}
</defs>'''


def keycap(x, y, label, c, w=None, h=32, size=14):
    w = w or (len(label) * 9 + 22)
    return (f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="7" fill="{c["cap"]}" stroke="{c["capEdge"]}"/>'
            f'<rect x="{x}" y="{y+h-3}" width="{w}" height="3" rx="1.5" fill="{c["capEdge"]}" opacity=".7"/>'
            f'<text class="med" x="{x+w/2}" y="{y+h/2+5}" text-anchor="middle" font-size="{size}" fill="{c["ink"]}">{label}</text>'), w


def keys(x, y, parts, c, gap=6):
    out, cx = [], x
    for p in parts:
        k, w = keycap(cx, y, p, c)
        out.append(k)
        cx += w + gap
    return ''.join(out), cx - x


# ── banner ──────────────────────────────────────────────────────────────────

def banner(theme, lang):
    c, l = T[theme], L[lang]
    W, H = 1280, 520
    kf = [(0, .18, 0), (.18, .18, 0), (.30, .92, 0), (.55, .92, 0), (.63, .46, 0), (.78, .46, 0), (.86, .92, 0), (.94, .18, 0), (1, .18, 0)]
    cores = [(0, c['accent']), (.26, c['tally']), (.6, c['accent']), (.82, c['tally']), (.93, c['accent'])]
    k1, w1 = keys(92, 356, ['Ctrl', 'Alt', 'R'], c)
    k2, w2 = keys(92 + w1 + 92, 356, ['Ctrl', 'Alt', 'A'], c)
    text_all = ''.join(l.values()) + 'SweaxIrisCtrlAltRA'
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" aria-label="SweaxIris — {l['tag']}">
{style(text_all)}
<style>.blink{{animation:b 1.2s ease-in-out infinite}}@keyframes b{{50%{{opacity:.25}}}}.rec{{animation:r 8s steps(1) infinite}}@keyframes r{{0%,26%{{opacity:0}}27%,62%{{opacity:1}}63%,80%{{opacity:0}}81%,93%{{opacity:1}}94%,100%{{opacity:0}}}}
.pause{{animation:p 8s steps(1) infinite}}@keyframes p{{0%,62%{{opacity:0}}63%,80%{{opacity:1}}81%,100%{{opacity:0}}}}</style>
{defs(c)}
<rect width="{W}" height="{H}" rx="24" fill="url(#bg)"/>
<ellipse cx="960" cy="250" rx="420" ry="300" fill="url(#ga)"/>
<ellipse cx="1050" cy="420" rx="300" ry="200" fill="url(#gt)"/>
<rect x=".5" y=".5" width="{W-1}" height="{H-1}" rx="23.5" fill="none" stroke="{c['edge']}"/>
<g transform="translate(0 0)">{iris(110, 112, 22, c, [(0,.42,0),(1,.42,0)], uid='lg', scale=False)}</g>
<text x="92" y="76" font-size="15" fill="{c['ink3']}">{l['kicker']}</text>
<text class="semi" x="146" y="128" font-size="52" letter-spacing="-1" fill="{c['ink']}">SweaxIris</text>
<text class="semi" x="92" y="218" font-size="36" letter-spacing="-.5" fill="{c['ink']}">{l['tag']}</text>
<text x="92" y="262" font-size="18" fill="{c['ink2']}">{l['sub1']}</text>
<text x="92" y="288" font-size="18" fill="{c['ink2']}">{l['sub2']}</text>
{k1}<text x="{92+w1+12}" y="377" font-size="15" fill="{c['ink3']}">{l['keys']}</text>
{k2}<text x="{92+w1+92+w2+12}" y="377" font-size="15" fill="{c['ink3']}">{l['region']}</text>
<text x="92" y="452" font-size="14.5" fill="{c['ink3']}">{l['facts']}</text>
<g filter="url(#sh)">{iris(960, 262, 190, c, kf, uid='bi', cores=cores)}</g>
<g opacity="0"><animate attributeName="opacity" dur="8s" repeatCount="indefinite" calcMode="discrete" keyTimes="0;.27;.63;.81;.94" values="0;1;0;1;0"/><rect x="884" y="472" width="152" height="30" rx="8" fill="{c['tally']}" fill-opacity=".16"/><circle class="blink" cx="902" cy="487" r="5" fill="{c['tally']}"/><text class="med" x="916" y="492" font-size="14" fill="{c['ink']}">{l['rec']} 00:01:23</text></g>
<g opacity="0"><animate attributeName="opacity" dur="8s" repeatCount="indefinite" calcMode="discrete" keyTimes="0;.63;.81" values="0;1;0"/><rect x="884" y="472" width="152" height="30" rx="8" fill="{c['accent']}" fill-opacity=".16"/><rect x="897" y="480" width="3.5" height="14" rx="1" fill="{c['accent']}"/><rect x="904" y="480" width="3.5" height="14" rx="1" fill="{c['accent']}"/><text class="med" x="916" y="492" font-size="14" fill="{c['ink']}">{l['paused']}</text></g>
</svg>'''


# ── feature vignettes ───────────────────────────────────────────────────────

ANIM = '''<style>
.flow{animation:flow 1.4s linear infinite}@keyframes flow{to{stroke-dashoffset:-20}}
.grow{animation:grow 5s cubic-bezier(.3,1.25,.45,1) infinite;transform-origin:100px 60px}
@keyframes grow{0%,10%{transform:scale(.05,.05);opacity:0}12%{opacity:1}45%,85%{transform:none;opacity:1}95%,100%{opacity:0}}
.m1{animation:m 0.9s ease-in-out infinite alternate}.m2{animation:m 1.3s ease-in-out infinite alternate-reverse}
@keyframes m{from{transform:scaleX(.35)}to{transform:scaleX(1)}}
.mute{animation:mu 4s steps(1) infinite}@keyframes mu{0%,49%{opacity:1}50%,100%{opacity:.18}}
.slash{animation:sl 4s steps(1) infinite}@keyframes sl{0%,49%{opacity:0}50%,100%{opacity:1}}
.press{animation:pr 3s cubic-bezier(.3,1.4,.5,1) infinite}@keyframes pr{0%,18%{transform:none}24%{transform:translateY(3px)}32%,100%{transform:none}}
.glow{animation:gl 3s ease-out infinite}@keyframes gl{0%,18%{opacity:0}26%{opacity:1}70%,100%{opacity:0}}
.gap{animation:gp 6s cubic-bezier(.45,0,.2,1) infinite}@keyframes gp{0%,30%{transform:translateX(70px)}55%,100%{transform:none}}
.hold{animation:hd 6s steps(1) infinite}@keyframes hd{0%,29%{opacity:1}30%,100%{opacity:0}}
.blink{animation:b 1.2s ease-in-out infinite}@keyframes b{50%{opacity:.25}}
</style>'''


def frame(c, inner, text='', w=420, h=236):
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}">
{style(text + 'NVENCAMFQSVx264MP4MKVG1G2G3G4G5PauseREC1280×720') }
{ANIM}
{defs(c)}
<rect width="{w}" height="{h}" rx="18" fill="url(#bg)"/>
<rect x=".5" y=".5" width="{w-1}" height="{h-1}" rx="17.5" fill="none" stroke="{c['edge']}"/>
{inner}
</svg>'''


def v_region(c):
    desk = f'<rect x="40" y="30" width="340" height="176" rx="10" fill="{c["panel"]}" stroke="{c["edge"]}"/>'
    bars = ''.join(f'<rect x="{70+i*26}" y="{170-h}" width="16" height="{h}" rx="3" fill="{c["accent"]}" fill-opacity=".45"/>' for i, h in enumerate([40, 70, 52, 96, 80, 110, 64, 88, 120, 74, 100]))
    sel = (f'<g transform="translate(60 52)"><g class="grow"><rect x="0" y="0" width="200" height="120" fill="none" stroke="{c["accent"]}" stroke-width="2"/>'
           f'<path d="M0,18 V0 H18 M182,0 H200 V18 M200,102 V120 H182 M18,120 H0 V102" fill="none" stroke="{c["accent"]}" stroke-width="4" stroke-linecap="round"/>'
           f'<rect x="0" y="-26" width="86" height="20" rx="5" fill="#0A0C0F" fill-opacity=".85"/><text class="med" x="8" y="-11" font-size="12" fill="#FFFFFF">1280×720</text></g></g>')
    veil = f'<path d="M40,30 H380 V206 H40 Z M60,52 V172 H260 V52 Z" fill="#05070A" fill-opacity=".38" fill-rule="evenodd"/>'
    return frame(c, desk + bars + veil + sel)


def v_pause(c):
    track = f'<rect x="40" y="96" width="340" height="44" rx="10" fill="{c["panel"]}" stroke="{c["edge"]}"/>'
    a = f'<rect x="50" y="106" width="140" height="24" rx="6" fill="{c["accent"]}" fill-opacity=".55"/>'
    b = f'<g class="gap"><rect x="196" y="106" width="174" height="24" rx="6" fill="{c["accent"]}" fill-opacity=".55"/></g>'
    hold = f'<g class="hold"><rect x="198" y="102" width="62" height="32" rx="6" fill="none" stroke="{c["ink3"]}" stroke-dasharray="4 4"/><rect x="221" y="111" width="5" height="14" rx="1.5" fill="{c["ink2"]}"/><rect x="231" y="111" width="5" height="14" rx="1.5" fill="{c["ink2"]}"/></g>'
    file = f'<text class="med" x="40" y="72" font-size="15" fill="{c["ink"]}">take.mp4</text><text x="40" y="176" font-size="12.5" fill="{c["ink3"]}">00:00 ───────────── 01:23</text>'
    return frame(c, track + a + b + hold + file, 'take.mp4───────────── ')


def v_encoders(c):
    chip = f'<rect x="160" y="70" width="100" height="100" rx="14" fill="{c["raised"]}" stroke="{c["edge"]}"/>' + ''.join(
        f'<rect x="{168+i*14}" y="62" width="6" height="8" rx="1" fill="{c["ink3"]}"/><rect x="{168+i*14}" y="170" width="6" height="8" rx="1" fill="{c["ink3"]}"/>' for i in range(6))
    chip += iris(210, 120, 30, c, [(0, .5, 0), (.5, .85, 20), (1, .5, 0)], dur=4, uid='ec', scale=False)
    lanes = ''
    names = ['NVENC', 'AMF', 'QSV', 'x264']
    for i, n in enumerate(names):
        y = 52 + i * 44
        lanes += f'<path d="M262,120 C 300,120 300,{y} 330,{y}" fill="none" stroke="{c["line"]}" stroke-width="2"/><path class="flow" d="M262,120 C 300,120 300,{y} 330,{y}" fill="none" stroke="{c["accent"]}" stroke-width="2" stroke-dasharray="4 16" stroke-linecap="round"/>'
        lanes += f'<text class="med" x="338" y="{y+5}" font-size="13" fill="{c["ink"]}">{n}</text>'
    src = f'<rect x="36" y="88" width="88" height="64" rx="8" fill="{c["panel"]}" stroke="{c["edge"]}"/><path d="M124,120 H158" stroke="{c["line"]}" stroke-width="2"/><path class="flow" d="M124,120 H158" stroke="{c["accent"]}" stroke-width="2" stroke-dasharray="4 16"/>'
    return frame(c, src + lanes + chip)


def v_audio(c):
    def strip(y, label, cls, mute=False):
        segs = ''.join(f'<rect x="{120+i*10}" y="{y}" width="7" height="12" rx="2" fill="{c["meter"] if i < 16 else c["mid"] if i < 20 else c["tally"]}"/>' for i in range(24))
        g = f'<g class="{cls}" style="transform-origin:120px {y}px">{segs}</g>'
        if mute:
            g = f'<g class="mute">{g}</g>'
        icon = f'<text class="med" x="40" y="{y+11}" font-size="13" fill="{c["ink"]}">{label}</text>'
        return icon + f'<rect x="118" y="{y-2}" width="244" height="16" rx="4" fill="{c["line"]}"/>' + g
    s = strip(84, 'System', 'm1') + strip(140, 'Mic', 'm2', True)
    s += f'<g class="slash"><path d="M372,132 l16,16 M388,132 l-16,16" stroke="{c["tally"]}" stroke-width="2.4" stroke-linecap="round"/></g>'
    return frame(c, s, 'SystemMic')


def v_macro(c):
    kb = f'<rect x="30" y="120" width="360" height="86" rx="12" fill="{c["panel"]}" stroke="{c["edge"]}"/>'
    for i, g in enumerate(['G1', 'G2', 'G3', 'G4', 'G5']):
        x = 44 + i * 64
        cls = ' class="press"' if i == 0 else ''
        kb += f'<g{cls}><rect x="{x}" y="138" width="54" height="40" rx="7" fill="{c["cap"]}" stroke="{c["capEdge"]}"/><text class="med" x="{x+27}" y="164" text-anchor="middle" font-size="14" fill="{c["ink"]}">{g}</text></g>'
    kb += f'<rect class="glow" x="42" y="136" width="58" height="44" rx="8" fill="none" stroke="{c["accent"]}" stroke-width="2.5"/>'
    arrow = f'<path d="M72,132 C 72,90 150,70 196,70" fill="none" stroke="{c["line"]}" stroke-width="2"/><path class="flow" d="M72,132 C 72,90 150,70 196,70" fill="none" stroke="{c["accent"]}" stroke-width="2" stroke-dasharray="4 16"/>'
    cap, w = keycap(204, 54, 'Pause', c)
    rec = f'<g class="glow"><circle cx="{204+w+30}" cy="70" r="7" fill="{c["tally"]}"/><text class="med" x="{204+w+44}" y="75" font-size="13" fill="{c["ink"]}">REC</text></g>'
    return frame(c, kb + arrow + cap + rec)


def v_hidden(c):
    scr = f'<rect x="40" y="40" width="160" height="104" rx="8" fill="{c["panel"]}" stroke="{c["edge"]}"/><rect x="60" y="112" width="120" height="20" rx="6" fill="{c["raised"]}" stroke="{c["accent"]}"/><circle cx="72" cy="122" r="4" fill="{c["tally"]}"/>'
    vid = f'<rect x="232" y="40" width="160" height="104" rx="8" fill="{c["panel"]}" stroke="{c["edge"]}"/><rect x="252" y="112" width="120" height="20" rx="6" fill="none" stroke="{c["ink3"]}" stroke-dasharray="4 4"/>'
    arrow = f'<path d="M204,92 H228" stroke="{c["line"]}" stroke-width="2"/><path class="flow" d="M204,92 H228" stroke="{c["accent"]}" stroke-width="2" stroke-dasharray="4 16"/>'
    labels = f'<text x="40" y="172" font-size="12.5" fill="{c["ink3"]}">screen</text><text x="232" y="172" font-size="12.5" fill="{c["ink3"]}">video</text>'
    return frame(c, scr + vid + arrow + labels, 'screenvideo')


# ── architecture ────────────────────────────────────────────────────────────

def arch(theme):
    c = T[theme]
    W, H = 1180, 500
    txt = []

    def box(x, y, w, h, title, lines, hue):
        t = f'<g filter="url(#sh)"><rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{c["panel"]}" stroke="{c["edge"]}"/></g><rect x="{x}" y="{y+16}" width="3" height="22" rx="1.5" fill="{hue}"/>'
        t += f'<text class="semi" x="{x+20}" y="{y+33}" font-size="16" fill="{c["ink"]}">{title}</text>'
        txt.append(title)
        for i, ln in enumerate(lines):
            t += f'<text x="{x+20}" y="{y+58+i*20}" font-size="12.5" fill="{c["ink2"]}">{ln}</text>'
            txt.append(ln)
        return t

    def wire(d, hue):
        return f'<path d="{d}" fill="none" stroke="{c["line"]}" stroke-width="2"/><path class="flow" d="{d}" fill="none" stroke="{hue}" stroke-width="2" stroke-dasharray="4 16" stroke-linecap="round"/>'

    acc, tal, ink = c['accent'], c['tally'], c['ink3']
    b = ''
    b += box(30, 40, 240, 130, 'Windows.Graphics.Capture', ['monitor · window · region', 'every refresh, with its', 'composition time'], acc)
    b += box(320, 40, 250, 130, 'Frame ring + clock', ['picks the frame composed at', 'each tick (24–240 fps)', 'D3D11 video engine → NV12'], acc)
    b += box(620, 40, 250, 130, 'Encoder (FFmpeg 7.1)', ['NVENC · AMF: GPU textures', 'Quick Sync · x264: staging', 'H.264 · HEVC · AV1'], acc)
    b += box(30, 300, 240, 150, 'WASAPI', ['speakers (loopback)', 'microphone', 'one app only', 'QPC timestamps'], tal)
    b += box(320, 300, 250, 150, 'Mixer', ['places packets by time', 'stretches drift ≤ 0.2 %', 'volume · mute ramps', 'mix + separate tracks'], tal)
    b += box(620, 300, 250, 150, 'AAC 48 kHz', ['128–320 kb/s', 'one encoder per track'], tal)
    b += box(920, 170, 230, 150, 'MP4 · MKV', ['one file, any pauses', 'BT.709 tags', 'hvc1 for Apple'], ink)
    b += f'<rect x="355" y="210" width="180" height="56" rx="12" fill="{c["raised"]}" stroke="{c["accent"]}" stroke-dasharray="5 5"/><text class="semi" x="445" y="236" text-anchor="middle" font-size="14" fill="{c["ink"]}">Timeline</text><text x="445" y="254" text-anchor="middle" font-size="12" fill="{c["ink2"]}">stops while paused</text>'
    txt.append('Timelinestops while paused')
    w = ''
    w += wire('M270 105 H320', acc) + wire('M570 105 H620', acc) + wire('M270 375 H320', tal) + wire('M570 375 H620', tal)
    w += wire('M870 105 C 900 105, 900 210, 920 210', acc) + wire('M870 375 C 900 375, 900 280, 920 280', tal)
    w += wire('M445 210 V170', ink) + wire('M445 266 V300', ink)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" aria-label="SweaxIris architecture">
{style(''.join(txt))}
<style>.flow{{animation:flow 1.4s linear infinite}}@keyframes flow{{to{{stroke-dashoffset:-20}}}}</style>
{defs(c)}
<rect width="{W}" height="{H}" rx="20" fill="url(#bg)"/>
<rect x=".5" y=".5" width="{W-1}" height="{H-1}" rx="19.5" fill="none" stroke="{c['edge']}"/>
{w}{b}
</svg>'''


def mark(theme, size=256):
    c = T['dark']
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 256 256">
<circle cx="128" cy="128" r="126" fill="#101317"/>
{iris(128, 128, 124, c, [(0, .5, 0), (1, .5, 0)], uid='mk')}
</svg>'''


if __name__ == '__main__':
    OUT = sys.argv[1]
    os.makedirs(OUT, exist_ok=True)
    for theme in ('dark', 'light'):
        for lang in ('en', 'tr'):
            open(os.path.join(OUT, f'banner-{lang}-{theme}.svg'), 'w', encoding='utf-8').write(banner(theme, lang))
        c = T[theme]
        for name, fn in (('region', v_region), ('pause', v_pause), ('encoders', v_encoders), ('audio', v_audio), ('macro', v_macro), ('hidden', v_hidden)):
            open(os.path.join(OUT, f'feature-{name}-{theme}.svg'), 'w', encoding='utf-8').write(fn(c))
        open(os.path.join(OUT, f'architecture-{theme}.svg'), 'w', encoding='utf-8').write(arch(theme))
    open(os.path.join(OUT, 'mark.svg'), 'w', encoding='utf-8').write(mark('dark'))
    print('ok', sorted(os.listdir(OUT)))
