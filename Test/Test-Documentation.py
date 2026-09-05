"""Check repository-owned Markdown links and local website structure without publishing."""
import hashlib
import html
import json
import re
import subprocess
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parent.parent
REPORT = ROOT / "artifacts/correctness-review/documentation-check.json"


def anchors(text):
    result, counts = set(), {}
    fenced = False
    for line in text.splitlines():
        if re.match(r"^\s*(```|~~~)", line):
            fenced = not fenced
        if fenced:
            continue
        match = re.match(r"^#{1,6}\s+(.+?)\s*#*\s*$", line)
        if match:
            value = html.unescape(re.sub(r"<[^>]*>", "", match[1])).lower()
            value = re.sub(r"[^\w\-\s]", "", value).replace(" ", "-")
            count = counts.get(value, 0)
            counts[value] = count + 1
            result.add(value if count == 0 else f"{value}-{count}")
    result.update(re.findall(r'(?:id|name)=[\'"]([^\'"]+)', text))
    return result


class Site(HTMLParser):
    def __init__(self):
        super().__init__()
        self.ids, self.urls, self.controls, self.examples = [], [], [], []
        self.example = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            self.ids.append(attrs["id"])
        for name in ("href", "src"):
            if name in attrs:
                self.urls.append(attrs[name])
        if "aria-controls" in attrs:
            self.controls.append(attrs["aria-controls"])
        if tag == "pre":
            self.example = []

    def handle_data(self, data):
        if self.example is not None:
            self.example.append(data)

    def handle_endtag(self, tag):
        if tag == "pre" and self.example is not None:
            self.examples.append("".join(self.example))
            self.example = None


def main():
    paths = subprocess.check_output(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z", "--", "*.md"], cwd=ROOT
    ).decode().split("\0")
    paths = sorted(set(filter(None, paths)))
    errors, documents, external = [], [], set()
    for name in paths:
        path = ROOT / name
        text = path.read_text(encoding="utf-8-sig")
        # Upstream prose and notices are inventoried but their relative links belong upstream.
        vendor = "/components/elf_loader/" in name or name.startswith("third_party/") or name.endswith("THIRD-PARTY-NOTICES.md")
        prose = re.sub(r"(?ms)^\s*```[^\n]*\n.*?^\s*```[^\n]*$", "", text)
        prose = re.sub(r"`[^`\n]+`", "", prose)
        links = re.findall(r"\[[^\]\n]*\]\((<[^>]+>|[^)\s]+)(?:\s+[^)]*)?\)", prose)
        links += re.findall(r"<(https?://[^>\s]+)>", prose)
        links += re.findall(r'<a\s[^>]*href=[\'"]([^\'"]+)', prose, re.IGNORECASE)
        for link in links:
            link = link.strip("<>")
            parsed = urlsplit(link)
            if parsed.scheme or parsed.netloc:
                external.add(link)
                continue
            if vendor:
                continue
            target = (path.parent / unquote(parsed.path)).resolve() if parsed.path else path
            if not target.exists():
                errors.append(f"{name}: missing {link}")
            elif parsed.fragment and target.suffix.lower() == ".md":
                if unquote(parsed.fragment) not in anchors(target.read_text(encoding="utf-8-sig")):
                    errors.append(f"{name}: missing anchor {link}")
        documents.append({"path": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                          "lines": len(text.splitlines()), "links": len(links), "upstreamOrNotice": vendor})
    site = Site()
    site.feed((ROOT / "website/index.html").read_text(encoding="utf-8-sig"))
    if len(site.ids) != len(set(site.ids)):
        errors.append("website/index.html: duplicate IDs")
    for control in site.controls:
        if control not in site.ids:
            errors.append(f"website/index.html: missing controlled panel {control}")
    for link in site.urls:
        parsed = urlsplit(link)
        if parsed.scheme or parsed.netloc:
            external.add(link)
        elif parsed.path and not (ROOT / "website" / unquote(parsed.path)).exists():
            errors.append(f"website/index.html: missing asset {link}")
        elif not parsed.path and parsed.fragment and parsed.fragment not in site.ids:
            errors.append(f"website/index.html: missing anchor {link}")
    subprocess.run(["node", "--check", str(ROOT / "website/script.js")], check=True)
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    (REPORT.parent / "website-examples.json").write_text(json.dumps(site.examples, indent=2), encoding="utf-8")
    REPORT.write_text(json.dumps({"documents": documents, "websiteExamples": len(site.examples),
                                 "externalLinks": sorted(external), "externalLinkStatus": "inventoried, not network-validated",
                                 "errors": errors, "passed": not errors}, indent=2), encoding="utf-8")
    print(f"Documentation: {len(documents)} Markdown files, {len(site.examples)} website examples, {len(errors)} errors")
    for error in errors:
        print(error)
    return bool(errors)


if __name__ == "__main__":
    raise SystemExit(main())
