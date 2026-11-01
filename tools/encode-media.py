"""Turns the --shots output into README / site media.

usage: python tools/encode-media.py docs/shots docs/media
  frames/<film>-<lang>-<theme>/*.png  ->  media/<film>-<lang>-<theme>.mp4 (H.264, 30 fps) and .gif (15 fps, 880 px)
  <still>-<lang>-<theme>.png          ->  media/<still>-<lang>-<theme>.png (width 1600, optimised)
"""
import os, shutil, subprocess, sys

FFMPEG = shutil.which("ffmpeg") or r"C:\ffmpeg\bin\ffmpeg.exe"


def run(args):
    subprocess.run([FFMPEG, "-v", "error", "-y"] + args, check=True)


def main(src, dst):
    os.makedirs(dst, exist_ok=True)
    frames = os.path.join(src, "frames")
    for film in sorted(os.listdir(frames)) if os.path.isdir(frames) else []:
        d = os.path.join(frames, film)
        if not os.path.isdir(d):
            continue
        pattern = os.path.join(d, "%05d.png")
        mp4 = os.path.join(dst, film + ".mp4")
        gif = os.path.join(dst, film + ".gif")
        run(["-framerate", "30", "-i", pattern, "-vf", "scale='trunc(min(1600,iw)/2)*2':-2:flags=lanczos,format=yuv420p",
             "-c:v", "libx264", "-preset", "slow", "-crf", "21", "-movflags", "+faststart", mp4])
        run(["-framerate", "30", "-i", pattern, "-vf",
             "fps=15,scale=880:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=160:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=rectangle",
             gif])
        print(film, os.path.getsize(mp4) // 1024, "KB mp4,", os.path.getsize(gif) // 1024, "KB gif")
    for f in sorted(os.listdir(src)):
        if f.endswith(".png"):
            out = os.path.join(dst, f)
            run(["-i", os.path.join(src, f), "-vf", "scale='min(1600,iw)':-2:flags=lanczos", "-compression_level", "9", out])
            print(f, os.path.getsize(out) // 1024, "KB")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
