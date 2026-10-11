#!/usr/bin/env python3
"""Find likely related Purge issues and pull requests without changing their state."""
import json, math, os, re, sys, urllib.error, urllib.request
from collections import Counter

API = "https://api.github.com"
REPO = os.environ["GITHUB_REPOSITORY"]
TOKEN = os.environ["GH_TOKEN"]
CURRENT = int(os.environ["ISSUE_NUMBER"])
MARKER = "<!-- purge-issue-relation-report -->"

def api(path):
    req = urllib.request.Request(API + path, headers={
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {TOKEN}",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "Purge-Issue-Relation-Analysis",
    })
    with urllib.request.urlopen(req, timeout=25) as response:
        return json.load(response)

def pages(path, limit):
    out = []
    for page in range(1, math.ceil(limit / 100) + 1):
        sep = "&" if "?" in path else "?"
        batch = api(f"{path}{sep}per_page=100&page={page}")
        if not isinstance(batch, list) or not batch:
            break
        out.extend(batch)
        if len(batch) < 100 or len(out) >= limit:
            break
    return out[:limit]

def plain(value):
    value = str(value or "")
    value = re.sub(r"https?://\S+", " ", value)
    value = re.sub(r"<[^>]+>", " ", value)
    value = value.replace(chr(96), " ")
    return re.sub(r"\s+", " ", value).strip().lower()

STOP = set("""
this that with from into when where what which have has were been your their there
issue issues bug bugs fix fixes fixed update updated please thanks the and for you
こと もの ため これ それ ところ 場合 対応 確認 修正 報告 現在 以下 について
できる できない する した して です ます ある いる なる ない という など
""".split())

def features(text):
    text = plain(text)
    counts = Counter()
    for token in re.findall(r"[a-z][a-z0-9_.:/+-]{1,}|(?:0x)?[0-9a-f]{3,}", text):
        token = token.strip("._:/+-")
        if len(token) > 1 and token not in STOP:
            counts["w:" + token] += 2
    # Character bigrams allow Japanese matching without an external tokenizer.
    compact = re.sub(r"[\W_]+", "", text, flags=re.UNICODE)
    for i in range(max(0, len(compact) - 1)):
        pair = compact[i:i + 2]
        if len(pair) == 2:
            counts["j:" + pair] += 1
    return counts

def cosine(a, b, idf):
    va = {k: v * idf.get(k, 1.0) for k, v in a.items()}
    vb = {k: v * idf.get(k, 1.0) for k, v in b.items()}
    na = math.sqrt(sum(v * v for v in va.values()))
    nb = math.sqrt(sum(v * v for v in vb.values()))
    if not na or not nb:
        return 0.0
    return sum(v * vb.get(k, 0.0) for k, v in va.items()) / (na * nb)

def issue_text(item):
    return f"{item.get('title', '')}\n{item.get('body', '')}"

def references_issue(text, number):
    text = text or ""
    return bool(re.search(rf"(?<![\w])#\s*{number}\b", text, re.I)
                or re.search(rf"/issues/{number}\b", text, re.I))

def render(related, prs):
    lines = [
        MARKER, "## 関連 Issue / 修正候補の自動調査", "",
        "> 自動候補です。類似度はタイトル・本文の語句の重なりを示す目安で、同一原因の証明ではありません。自動クローズやラベル変更は行いません。",
        "", "### 関連する可能性のある Issue",
    ]
    if related:
        for score, item in related:
            title = (item.get("title") or "(タイトルなし)").replace("[", r"\[").replace("]", r"\]")
            lines.append(f"- [#{item['number']}: {title}]({item['html_url']}) — 類似度目安 {round(score * 100)}%")
    else:
        lines.append("- 強い候補は見つかりませんでした。")
    lines += ["", "### 関連する可能性のある PR"]
    if prs:
        for score, item, reason in prs:
            title = (item.get("title") or "(タイトルなし)").replace("[", r"\[").replace("]", r"\]")
            suffix = f" — {reason}" if reason else f" — 類似度目安 {round(score * 100)}%"
            lines.append(f"- [#{item['number']}: {title}]({item['html_url']}){suffix}")
    else:
        lines.append("- 強い候補は見つかりませんでした。")
    lines += ["", "この結果は検索の補助です。関連性・共通原因・修正完了の判断は人が確認してください。"]
    return "\n".join(lines)

def main():
    current = api(f"/repos/{REPO}/issues/{CURRENT}")
    if "pull_request" in current:
        return
    current_features = features(issue_text(current))
    issues = pages(f"/repos/{REPO}/issues?state=open&sort=updated&direction=desc", 300)
    issues = [x for x in issues if "pull_request" not in x and x.get("number") != CURRENT]
    pull_requests = pages(f"/repos/{REPO}/pulls?state=all&sort=updated&direction=desc", 100)
    pull_requests = [x for x in pull_requests if x.get("state") == "open" or x.get("merged_at")]

    issue_features = {x["number"]: features(issue_text(x)) for x in issues}
    pr_features = {x["number"]: features(issue_text(x)) for x in pull_requests}
    corpus = [current_features] + list(issue_features.values()) + list(pr_features.values())
    df = Counter()
    for doc in corpus:
        df.update(set(doc))
    total = len(corpus)
    idf = {term: math.log(1 + total / (1 + freq)) + 1 for term, freq in df.items()}

    related = []
    for item in issues:
        score = cosine(current_features, issue_features[item["number"]], idf)
        if score >= 0.12:
            related.append((score, item))
    related.sort(key=lambda x: (-x[0], x[1]["number"]))
    related = related[:6]

    prs = []
    for item in pull_requests:
        number = item["number"]
        if references_issue(item.get("body", ""), CURRENT) or references_issue(item.get("title", ""), CURRENT):
            prs.append((1.0, item, f"本文が #{CURRENT} を参照"))
            continue
        score = cosine(current_features, pr_features[number], idf)
        if score >= 0.10:
            prs.append((score, item, ""))
    prs.sort(key=lambda x: (not bool(x[2]), -x[0], x[1]["number"]))
    prs = prs[:6]
    body = render(related, prs)

    comments = pages(f"/repos/{REPO}/issues/{CURRENT}/comments", 100)
    existing = next((c for c in reversed(comments)
                     if MARKER in (c.get("body") or "")
                     and (c.get("user") or {}).get("type") == "Bot"), None)
    payload = json.dumps({"body": body}).encode()
    headers = {
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {TOKEN}",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "Purge-Issue-Relation-Analysis",
        "Content-Type": "application/json",
    }
    endpoint = (f"/repos/{REPO}/issues/comments/{existing['id']}" if existing
                else f"/repos/{REPO}/issues/{CURRENT}/comments")
    req = urllib.request.Request(API + endpoint, data=payload, headers=headers,
                                 method="PATCH" if existing else "POST")
    with urllib.request.urlopen(req, timeout=25) as response:
        response.read()
    print(f"Updated relation report for #{CURRENT}: {len(related)} issue candidates, {len(prs)} PR candidates.")

if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as exc:
        print(f"GitHub API error: HTTP {exc.code}", file=sys.stderr)
        if exc.code == 403:
            print("Check Actions permissions and API rate limits.", file=sys.stderr)
        raise
