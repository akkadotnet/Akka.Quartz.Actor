#!/usr/bin/env python3
"""Renders run-walkthrough.sh transcripts as terminal screenshots for docs/upgrading-to-quartz4.md.

    python3 render-screenshots.py <transcripts-directory> <output-directory> [chrome-executable]

The Chrome or Chromium executable can also be given with the CHROME environment variable.
"""
import html
import os
import pathlib
import re
import subprocess
import sys
import textwrap

TITLES = {
    "01-legacy-app": "Your Quartz 3 application (Akka.Quartz.Actor 1.5.59, binary storage)",
    "02-baseline": "Before you start: rehearsal copy and baseline",
    "03-audit-before": "Step 1: audit the rehearsal copy",
    "04-fix-cron": "Step 2: replace the rejected schedule while Quartz 3 is available",
    "05-dry-run-mistakes": "Step 3a: common mistakes",
    "05-dry-run": "Step 3a: dry run",
    "06-apply": "Step 3b: convert",
    "07-audit-after": "Step 4: audit again",
    "08-schema": "Step 5: official schema upgrade script",
    "09-new-app": "Step 7: the upgraded application (Akka.Quartz.Actor 1.5.71-beta1, Quartz 4)",
    "10-verify": "Step 7: compare with the baseline",
}
COLUMNS = 108
FONT_SIZE = 13
LINE_HEIGHT = 19
CHROME_HEIGHT = 36 + 2 * 18  # title bar plus vertical padding
WIDTH = 940


def wrap_command(command):
    """Splits a long command at argument boundaries with shell line continuations."""
    tokens = re.findall(r'"[^"]*"|\S+', command)
    # Keep each option next to its value.
    grouped = []
    for token in tokens:
        if grouped and grouped[-1].startswith("--") and " " not in grouped[-1] and not token.startswith("-"):
            grouped[-1] += " " + token
        else:
            grouped.append(token)
    lines, current = [], "$"
    for token in grouped:
        candidate = f"{current} {token}"
        if len(candidate) <= COLUMNS - 2 or current == "$":
            current = candidate
            continue
        lines.append(current + " \\")
        current = "    " + token
    lines.append(current)
    # A quoted argument longer than a line (such as SQL) wraps inside its quotes, where no continuation is needed.
    wrapped = []
    for line in lines:
        while len(line) > COLUMNS:
            cut = line.rfind(" ", 0, COLUMNS)
            wrapped.append(line[:cut])
            line = "      " + line[cut + 1:]
        wrapped.append(line)
    return wrapped


def wrap_output(line):
    """Wraps long output at word boundaries, keeping its indentation."""
    if len(line) <= COLUMNS:
        return [line]
    indent = line[:len(line) - len(line.lstrip())]
    return textwrap.wrap(line, COLUMNS, subsequent_indent=indent + "  ", break_on_hyphens=False)


def render_lines(text):
    rows = []
    for line in text.rstrip("\n").split("\n"):
        if line.startswith("$ "):
            for index, part in enumerate(wrap_command(line[2:])):
                body = html.escape(part[1:] if index == 0 else part)
                prompt = '<span class="prompt">$</span>' if index == 0 else ""
                rows.append(f'{prompt}<span class="command">{body}</span>')
        elif re.fullmatch(r"\(exit code \d+\)", line):
            rows.append(f'<span class="exit">{html.escape(line)}</span>')
        else:
            rows.extend(f'<span class="output">{html.escape(part)}</span>' for part in wrap_output(line))
    while rows and rows[-1] == '<span class="output"></span>':
        rows.pop()
    return rows


def page(title, rows):
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>
html, body {{ margin: 0; background: #ffffff; }}
.window {{ margin: 0; background: #1e1f24; border-radius: 10px; overflow: hidden;
  font: {FONT_SIZE}px/{LINE_HEIGHT}px "DejaVu Sans Mono", "Cascadia Mono", Menlo, Consolas, monospace; }}
.bar {{ height: 36px; background: #2d2f36; display: flex; align-items: center; padding: 0 14px; gap: 8px; }}
.dot {{ width: 12px; height: 12px; border-radius: 50%; }}
.title {{ color: #b9bcc6; font: 12px/36px system-ui, sans-serif; margin-left: 10px; white-space: nowrap; overflow: hidden; }}
pre {{ margin: 0; padding: 18px 20px; white-space: pre; color: #d4d7de; }}
.prompt {{ color: #7ee787; font-weight: bold; margin-right: 1ch; }}
.command {{ color: #ffffff; font-weight: bold; }}
.output {{ color: #c9ccd4; }}
.exit {{ color: #ff9b8e; }}
</style></head><body><div class="window"><div class="bar">
<span class="dot" style="background:#ff5f57"></span><span class="dot" style="background:#febc2e"></span>
<span class="dot" style="background:#28c840"></span><span class="title">{html.escape(title)}</span></div>
<pre>{chr(10).join(rows)}</pre></div></body></html>"""


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    source, target = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    chrome = sys.argv[3] if len(sys.argv) > 3 else os.environ.get("CHROME")
    if not chrome:
        sys.exit("Pass the Chrome/Chromium executable as the third argument or set CHROME.")
    target.mkdir(parents=True, exist_ok=True)
    for transcript in sorted(source.glob("*.txt")):
        rows = render_lines(transcript.read_text())
        height = CHROME_HEIGHT + LINE_HEIGHT * len(rows)
        source_page = target / f"{transcript.stem}.html"
        source_page.write_text(page(TITLES.get(transcript.stem, transcript.stem), rows))
        output = target / f"{transcript.stem}.png"
        # The page is a local, static file generated above, so Chrome's sandbox is not needed.
        subprocess.run([chrome, "--headless", "--no-sandbox", "--hide-scrollbars", "--force-device-scale-factor=2",
                        f"--window-size={WIDTH},{height}", f"--screenshot={output}", source_page.resolve().as_uri()],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        source_page.unlink()
        print(output)


if __name__ == "__main__":
    main()
