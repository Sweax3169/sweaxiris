"""Checks SweaxIris self-test recordings: frame pacing, drops/repeats, A/V sync across pauses.

usage: python tools/analyze.py DIR   (DIR holds report.json written by SweaxIris.exe --selftest DIR)

Each test picture carries a 24-bit stamp of the wall time in ms, a white square for the first 100 ms of every
second and the sound has a 1 kHz tone for the same 100 ms. So per recording we can tell
  * every frame's real moment (and whether a frame repeats or is missing),
  * how far apart flash and tone land in the file, before and after each pause.
"""
import json, subprocess, sys, os, statistics, shutil

FFMPEG = shutil.which("ffmpeg") or r"C:\ffmpeg\bin\ffmpeg.exe"
FFPROBE = shutil.which("ffprobe") or r"C:\ffmpeg\bin\ffprobe.exe"


def probe(path):
    out = subprocess.run([FFPROBE, "-v", "error", "-show_streams", "-show_format", "-of", "json", path], capture_output=True, text=True)
    return json.loads(out.stdout)


def frames_gray(path, w, h):
    sw = 320
    sh = int(round(h * sw / w / 2) * 2)
    p = subprocess.run([FFMPEG, "-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-vf", f"scale={sw}:{sh}:flags=area", "-f", "rawvideo", "-pix_fmt", "gray", "-"], capture_output=True)
    data = p.stdout
    n = len(data) // (sw * sh)
    return [data[i * sw * sh:(i + 1) * sw * sh] for i in range(n)], sw, sh


def pts_list(path):
    out = subprocess.run([FFPROBE, "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "json", path], capture_output=True, text=True)
    return [float(f["pts_time"]) for f in json.loads(out.stdout).get("frames", []) if "pts_time" in f]


def audio(path, track=0):
    p = subprocess.run([FFMPEG, "-v", "error", "-i", path, "-map", f"0:a:{track}", "-ac", "1", "-ar", "48000", "-f", "f32le", "-"], capture_output=True)
    import array
    a = array.array("f")
    a.frombytes(p.stdout[: len(p.stdout) // 4 * 4])
    return a


def onsets_audio(a, thresh):
    out, quiet, i = [], 4801, 0
    n = len(a)
    while i < n:
        if abs(a[i]) > thresh:
            if quiet > 4800:  # 100 ms of quiet before
                out.append(i / 48000)
            quiet = 0
        else:
            quiet += 1
        i += 1
    return out


def analyze(t):
    path = t["path"]
    if not os.path.exists(path):
        return {"test": t["test"], "error": "missing file"}
    info = probe(path)
    v = next(s for s in info["streams"] if s["codec_type"] == "video")
    auds = [s for s in info["streams"] if s["codec_type"] == "audio"]
    w, h = int(v["width"]), int(v["height"])
    frames, sw, sh = frames_gray(path, w, h)
    pts = pts_list(path)
    fps = t["fps"]
    scale = sw / w
    real = t.get("real", False)
    cell = 22 if real else max(8, h // 24)
    flash_size = 135 if real else h // 4

    def stamp(f):
        ms = 0
        y = int((1.5 * cell) * scale)
        for i in range(24):
            x = int((cell + i * cell + cell / 2) * scale)
            ms = ms * 2 + (1 if f[y * sw + x] > 128 else 0)
        return ms

    def flash(f):
        cx = int((w - cell - flash_size / 2) * scale)
        cy = int((h - cell - flash_size / 2) * scale)
        r = max(2, int(flash_size * scale / 4))
        vals = [f[(cy + dy) * sw + cx + dx] for dy in range(-r, r) for dx in range(-r, r)]
        return sum(vals) / len(vals)

    stamps = [stamp(f) for f in frames]
    pauses = t.get("pauses", [])

    def active(wall_s):
        a = wall_s
        for p0, p1 in pauses:
            if wall_s >= p1:
                a -= p1 - p0
        return a

    # per frame: how far the picture's moment is from its place in the file
    lag = []
    repeats = jumps = 0
    for k, ms in enumerate(stamps):
        tk = pts[k] if k < len(pts) else k / fps
        lag.append(active(ms / 1000) - tk)
        if k:
            d = ms - stamps[k - 1]
            if d == 0:
                repeats += 1
            elif d > 1000 / fps * 1.6 and not any(abs(ms / 1000 - p1) < 0.05 for _, p1 in pauses):
                jumps += 1
    # flash onsets (video) and tone onsets (audio)
    lum = [flash(f) for f in frames]
    v_on = [pts[k] if k < len(pts) else k / fps for k in range(1, len(lum)) if lum[k] > 128 and lum[k - 1] <= 128]
    if lum and lum[0] > 128:
        v_on.insert(0, 0.0)
    res = {
        "test": t["test"], "encoder": t["encoder"], "size": f"{w}x{h}", "fps": fps,
        "codec": v["codec_name"], "pix_fmt": v.get("pix_fmt"), "color": f'{v.get("color_space")}/{v.get("color_range")}',
        "frames": len(frames), "expected_frames": round(t["duration"] * fps), "duration": float(info["format"]["duration"]),
        "report_dropped": t["dropped"], "report_late": t["late"], "repeats_in_file": repeats, "jumps_in_file": jumps,
        "picture_lag_ms": [round(min(lag) * 1000, 1), round(statistics.median(lag) * 1000, 1), round(max(lag) * 1000, 1)] if lag else None,
        "mbps": round(t["bytes"] * 8 / max(0.001, t["duration"]) / 1e6, 2),
        "audio_tracks": len(auds),
    }
    for ti, s in enumerate(auds):
        a = audio(path, ti)
        # MKV keeps the AAC priming samples before 0 (MP4 hides them with an edit list)
        a_start = float(s.get("start_time", 0) or 0)
        a_on = [x + a_start for x in onsets_audio(a, 0.003 if (real or t.get("quiet")) else 0.1)]
        pairs = []
        for ao in a_on:
            if not v_on:
                break
            vo = min(v_on, key=lambda x: abs(x - ao))
            if abs(vo - ao) < 0.4:
                pairs.append(round((ao - vo) * 1000, 1))
        res[f"track{ti}"] = {"title": s.get("tags", {}).get("title"), "audio_s": round(len(a) / 48000, 3), "tones": len(a_on), "flashes": len(v_on),
                             "av_offset_ms (audio - video)": pairs}
    return res


def main():
    d = sys.argv[1]
    rep = json.load(open(os.path.join(d, "report.json"), encoding="utf-8"))
    out = []
    for t in rep:
        if "path" not in t or t.get("test") == "probe":
            continue
        r = analyze(t)
        out.append(r)
        print(json.dumps(r, ensure_ascii=False))
    json.dump(out, open(os.path.join(d, "analysis.json"), "w", encoding="utf-8"), indent=1, ensure_ascii=False)


if __name__ == "__main__":
    main()
