#!/usr/bin/env python3
"""
Builds Qalam's built-in word pairs: src/Qalam.Core/Data/bigram_{ru,en,tg}.txt.gz

Runs in GitHub Actions (.github/workflows/bigrams.yml), which has open internet access.
Streams public corpora, counts pairs of neighbouring words inside a sentence (commas do not break a
pair, like in Qalam itself), keeps only words of Qalam's own dictionaries and writes for each common
word its usual followers:

    prev<TAB>next zc next zc ...      zc = log10 P(next | prev) + 9  (the Zipf scale of the word lists)

Every source is optional: what cannot be downloaded is skipped and reported. A language whose sources
all fail keeps its previous file.

Sources (licences in Data/LICENSES.txt):
  OpenSubtitles 2018 monolingual (OPUS)   — conversational Russian / English
  Leipzig Corpora Collection (news, web)  — CC BY
  FineWeb-2 (HuggingFaceFW), Wikipedia    — Tajik web text and encyclopedia (ODC-By / CC BY-SA)
"""
import collections
import gzip
import io
import json
import math
import os
import re
import sys
import tarfile
import tempfile
import time
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
DATA = os.path.join(ROOT, "src", "Qalam.Core", "Data")

MAX_TOKENS = {"ru": 45_000_000, "en": 45_000_000, "tg": 60_000_000}
MIN_TOKENS = 2_000_000          # less than this → the file would be noise; keep the old one
MAX_PREV = {"ru": 22_000, "en": 18_000, "tg": 22_000}   # most frequent previous words kept
TOP_K = 20                      # followers per word
MIN_NEXT_LEN = 4                # completion starts after 3 letters → shorter followers are never shown
MIN_PAIR = 3                    # a pair must be seen at least this often
MIN_PREV = 30                   # and its first word this often

OPUS = "https://object.pouta.csc.fi/OPUS-OpenSubtitles/v2018/mono/{}.txt.gz"
LEIPZIG = "https://downloads.wortschatz-leipzig.de/corpora/{}.tar.gz"
HF = "https://huggingface.co"

SOURCES = {
    "ru": [
        ("lines_gz", OPUS.format("ru"), 6_000_000),
        ("leipzig", ["rus_news_2023_1M", "rus_news_2022_1M", "rus_news_2021_1M", "rus_news_2020_1M"], None),
        ("leipzig", ["rus-ru_web-public_2019_1M", "rus_web-public_2019_1M", "rus_wikipedia_2021_1M"], None),
    ],
    "en": [
        ("lines_gz", OPUS.format("en"), 6_000_000),
        ("leipzig", ["eng_news_2023_1M", "eng_news_2020_1M", "eng_news_2019_1M"], None),
        ("leipzig", ["eng-com_web-public_2018_1M", "eng_web-public_2018_1M", "eng_wikipedia_2016_1M"], None),
    ],
    "tg": [
        ("hf_dir", ("HuggingFaceFW/fineweb-2", "data/tgk_Cyrl/train"), None),
        ("hf_dir", ("wikimedia/wikipedia", "20231101.tg"), None),
        ("leipzig", ["tgk_news_2020_300K", "tgk_news_2019_300K", "tgk_news_2016_300K", "tgk_newscrawl_2016_300K", "tgk_newscrawl_2019_300K"], None),
        ("leipzig", ["tgk-tj_web_2015_300K", "tgk_web_2015_300K", "tgk_community_2017", "tgk_wikipedia_2021_100K"], None),
    ],
}

WORD = re.compile(r"[^\W\d_]+(?:[-'’][^\W\d_]+)*")
# a sentence or clause ends here; commas and spaces do not break a pair
BREAK = re.compile(r"[.!?;:…()\[\]{}«»\"“”„/|<>•·\n\r\t]+|\s[-—–]\s|^[-—–]\s")


def log(*a):
    print(*a, flush=True)


def open_url(url, timeout=120):
    req = urllib.request.Request(url, headers={"User-Agent": "Qalam bigram builder (github.com/ustokhona/qalam)"})
    return urllib.request.urlopen(req, timeout=timeout)


def load_vocab(lang):
    vocab = {}
    with gzip.open(os.path.join(DATA, f"{lang}.txt.gz"), "rt", encoding="utf-8") as f:
        for line in f:
            w, _, z = line.rstrip("\n").partition("\t")
            if w and z:
                vocab[w] = float(z)
    return vocab


# ---------------------------------------------------------------- sources → text lines
def lines_gz(url, max_lines):
    with open_url(url) as r:
        with gzip.GzipFile(fileobj=r) as g:
            for i, raw in enumerate(io.TextIOWrapper(g, encoding="utf-8", errors="ignore")):
                if max_lines and i >= max_lines:
                    break
                yield raw


def leipzig(names, _):
    for name in names:
        url = LEIPZIG.format(name)
        try:
            r = open_url(url)
        except Exception as e:
            log(f"    {name}: {e}")
            continue
        log(f"    {name}: downloading")
        with r, tarfile.open(fileobj=r, mode="r|gz") as tar:
            for m in tar:
                if not m.name.endswith("-sentences.txt"):
                    continue
                f = tar.extractfile(m)   # a stream member: not seekable, so no TextIOWrapper
                for raw in f:
                    _, _, sent = raw.decode("utf-8", "ignore").partition("\t")
                    yield sent
        return   # one corpus of the group is enough
    raise RuntimeError("none of the Leipzig names exist")


def hf_dir(spec, _):
    repo, path = spec
    with open_url(f"{HF}/api/datasets/{repo}/tree/main/{path}") as r:
        items = json.load(r)
    files = sorted(i["path"] for i in items if i.get("type") == "file" and i["path"].endswith(".parquet"))
    if not files:
        raise RuntimeError(f"no parquet files in {repo}/{path}")
    import pyarrow.parquet as pq
    for p in files:
        log(f"    {repo}/{p}")
        with tempfile.NamedTemporaryFile(suffix=".parquet", delete=False) as tmp:
            with open_url(f"{HF}/datasets/{repo}/resolve/main/{p}", timeout=600) as r:
                while True:
                    chunk = r.read(1 << 20)
                    if not chunk:
                        break
                    tmp.write(chunk)
            name = tmp.name
        try:
            pf = pq.ParquetFile(name)
            for batch in pf.iter_batches(columns=["text"], batch_size=2000):
                for doc in batch.column(0).to_pylist():
                    if doc:
                        yield from doc.split("\n")
        finally:
            os.unlink(name)


KINDS = {"lines_gz": lines_gz, "leipzig": leipzig, "hf_dir": hf_dir}


# ---------------------------------------------------------------- counting
def count(lang, vocab):
    uni = collections.Counter()
    pairs = collections.Counter()
    tokens = 0
    report = []
    for kind, spec, limit in SOURCES[lang]:
        if tokens >= MAX_TOKENS[lang]:
            break
        label = spec if isinstance(spec, str) else (spec[0] if isinstance(spec, list) else "/".join(spec))
        start, t0 = tokens, time.time()
        try:
            for line in KINDS[kind](spec, limit):
                for seg in BREAK.split(line):
                    prev = None
                    for m in WORD.finditer(seg):
                        w = m.group(0).lower().replace("’", "'")
                        if w not in vocab:
                            prev = None
                            continue
                        tokens += 1
                        uni[w] += 1
                        if prev is not None:
                            pairs[(prev, w)] += 1
                        prev = w if len(w) >= 2 else None
                if tokens >= MAX_TOKENS[lang]:
                    break
                if len(pairs) > 25_000_000:   # keep memory in check: forget pairs seen once
                    for k in [k for k, v in pairs.items() if v == 1]:
                        del pairs[k]
            report.append(f"{label}: {tokens - start:,} tokens in {time.time() - t0:.0f}s")
        except Exception as e:
            report.append(f"{label}: FAILED after {tokens - start:,} tokens ({e})")
        log(f"  [{lang}] {report[-1]}")
    return uni, pairs, tokens, report


def write(lang, uni, pairs):
    followers = collections.defaultdict(list)
    for (a, b), n in pairs.items():
        if n >= MIN_PAIR and len(b) >= MIN_NEXT_LEN:
            followers[a].append((n, b))
    prevs = sorted((w for w in followers if uni[w] >= MIN_PREV), key=lambda w: -uni[w])[:MAX_PREV[lang]]
    path = os.path.join(DATA, f"bigram_{lang}.txt.gz")
    lines = 0
    with gzip.open(path, "wt", encoding="utf-8", compresslevel=9) as f:
        for a in sorted(prevs):
            total = uni[a]
            best = sorted(followers[a], reverse=True)[:TOP_K]
            parts = [f"{b} {math.log10(n / total) + 9:.2f}" for n, b in best]
            f.write(a + "\t" + " ".join(parts) + "\n")
            lines += 1
    log(f"  [{lang}] wrote {path}: {lines:,} words, {os.path.getsize(path) / 1e6:.2f} MB")
    return lines


def main():
    langs = sys.argv[1:] or ["tg", "ru", "en"]
    summary = {}
    for lang in langs:
        log(f"== {lang}")
        vocab = load_vocab(lang)
        uni, pairs, tokens, report = count(lang, vocab)
        if tokens < MIN_TOKENS:
            log(f"  [{lang}] only {tokens:,} tokens — keeping the previous file")
            summary[lang] = {"tokens": tokens, "written": False, "sources": report}
            continue
        n = write(lang, uni, pairs)
        summary[lang] = {"tokens": tokens, "written": True, "words": n, "sources": report}
        del uni, pairs
    with open(os.path.join(os.path.dirname(__file__), "last_build.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=1)
    log(json.dumps(summary, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()
