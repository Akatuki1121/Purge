#!/usr/bin/env python3
"""Find only evidence-backed issue relations using targeted GitHub search."""
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

API = "https://api.github.com"
REPO = os.environ.get("GITHUB_REPOSITORY", "")
TOKEN = os.environ.get("GH_TOKEN", "")
CURRENT = int(os.environ.get("ISSUE_NUMBER", "0") or 0)
MARKER = "<!-- purge-issue-relation-report -->"
INPUT_MARKER = "<!-- purge-issue-relation-input:"
CATEGORY_LABELS = {"installer", "update", "ui"}

FIELD_ALIASES = {
    "actual": ("実際の結果", "actual result", "actual behavior", "what happened"),
    "logs": ("関連ログ・スクリーンショット・エラーメッセージ", "logs", "error message", "exception"),
    "expected": ("期待する結果", "expected result", "expected behavior"),
    "steps": ("再現手順", "steps to reproduce", "reproduction steps"),
    "windows": ("windows のバージョン", "windows version"),
    "purge_version": ("purge のバージョン", "purge version"),
    "from_version": ("更新元のバージョン", "update from"),
    "to_version": ("更新先のバージョン", "update to"),
}

# Only a full-width 32-bit hexadecimal error identifier is accepted as standalone evidence.
# Generic exception class names and common MSI codes (for example 1603) are not distinctive enough.
SIGNATURE_PATTERNS = (re.compile(r"\b0x[0-9a-f]{8}\b", re.I),)
REFERENCE_PATTERN = re.compile(r"(?<![\w])#\s*(\d+)\b|/issues/(\d+)\b", re.I)


def api(path, method="GET", payload=None):
    if not TOKEN:
        raise RuntimeError("GH_TOKEN is required")
    data = json.dumps(payload).encode("utf-8") if payload is not None else None
    headers = {
        "Accept": "application/vnd.github+json",
        "Authorization": f"Bearer {TOKEN}",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "Purge-Issue-Relation-Analysis",
    }
    if data is not None:
        headers["Content-Type"] = "application/json"
    request = urllib.request.Request(API + path, data=data, headers=headers, method=method)
    with urllib.request.urlopen(request, timeout=25) as response:
        raw = response.read()
        return json.loads(raw) if raw else {}


def normalize(value):
    value = str(value or "").lower()
    value = re.sub(r"https?://\S+", " ", value)
    value = re.sub(r"<[^>]+>", " ", value)
    value = re.sub(r"\s+", " ", value).strip()
    return value


def parse_fields(body):
    """Parse GitHub Issue Forms headings without depending on their order."""
    sections = {}
    current = None
    chunks = []
    for line in (body or "").splitlines():
        heading = re.match(r"^#{1,6}\s+(.+?)\s*#*\s*$", line)
        if heading:
            if current:
                sections[current] = "\n".join(chunks).strip()
            current = normalize(heading.group(1)).rstrip(":：")
            chunks = []
        elif current:
            chunks.append(line)
    if current:
        sections[current] = "\n".join(chunks).strip()

    result = {}
    for key, aliases in FIELD_ALIASES.items():
        for heading, value in sections.items():
            if any(heading == normalize(alias).rstrip(":：") for alias in aliases):
                result[key] = value
                break
    return result


def extract_signatures(text):
    found = set()
    for pattern in SIGNATURE_PATTERNS:
        found.update(normalize(match) for match in pattern.findall(text or ""))
    return found


def issue_signatures(item):
    fields = parse_fields(item.get("body", ""))
    evidence = "\n".join((fields.get("actual", ""), fields.get("logs", "")))
    return extract_signatures(evidence)


def relation_evidence(current, candidate):
    """Return a strict match reason, or None; never infer a relation from general similarity."""
    current_fields = parse_fields(current.get("body", ""))
    candidate_fields = parse_fields(candidate.get("body", ""))
    same_signatures = issue_signatures(current) & issue_signatures(candidate)
    if same_signatures:
        return "一致した 32-bit エラー識別子: " + ", ".join(sorted(same_signatures))
    current_actual = normalize(current_fields.get("actual", ""))
    candidate_actual = normalize(candidate_fields.get("actual", ""))
    if current_actual and len(current_actual) >= 16 and current_actual == candidate_actual:
        return "Issueフォームの「実際の結果」が正規化後に完全一致"
    return None


def extract_issue_refs(text, current_number):
    refs = set()
    for match in REFERENCE_PATTERN.finditer(text or ""):
        number = int(match.group(1) or match.group(2))
        if number != current_number:
            refs.add(number)
    return refs


def labels_of(item):
    labels = item.get("labels") or []
    return {label.get("name", "") if isinstance(label, dict) else str(label) for label in labels}


def fingerprint(issue):
    material = "\n".join((
        issue.get("title", ""),
        issue.get("body", ""),
        ",".join(sorted(labels_of(issue))),
    ))
    return hashlib.sha256(material.encode("utf-8")).hexdigest()[:20]


def search_items(query):
    path = "/search/issues?" + urllib.parse.urlencode({"q": query, "per_page": 50})
    result = api(path)
    return result.get("items", [])


def direct_issue(number):
    try:
        item = api(f"/repos/{REPO}/issues/{number}")
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return None
        raise
    return None if "pull_request" in item else item


def find_issue_relations(issue):
    current_number = issue["number"]
    current_labels = labels_of(issue)
    category_labels = current_labels & CATEGORY_LABELS
    relations = {}

    # Explicit references are intentional links and do not require repository-wide search.
    refs = extract_issue_refs(issue.get("title", "") + "\n" + issue.get("body", ""), current_number)
    for number in sorted(refs):
        item = direct_issue(number)
        if item:
            relations[number] = {
                "item": item,
                "kind": "明示的な Issue 参照",
                "evidence": f"本文またはタイトルに #{number} の参照があります。",
            }

    # Search only for exact, distinctive evidence; never enumerate the repository's issues.
    current_signatures = issue_signatures(issue)
    current_fields = parse_fields(issue.get("body", ""))
    current_actual = normalize(current_fields.get("actual", ""))
    queries = [f'"{signature}"' for signature in sorted(current_signatures)[:3]]
    if len(current_actual) >= 16 and len(current_actual) <= 100:
        queries.append(f'"{current_actual}"')
    for term_query in queries[:4]:
        query = f"repo:{REPO} is:issue in:title,body {term_query}"
        categories = sorted(category_labels) or [None]
        for category in categories:
            scoped_query = query + (f" label:{category}" if category else "")
            for item in search_items(scoped_query):
                if item.get("number") == current_number or "pull_request" in item:
                    continue
                evidence = relation_evidence(issue, item)
                if evidence:
                    relations.setdefault(item["number"], {
                        "item": item,
                        "kind": "同じ症状の強い一致",
                        "evidence": evidence,
                    })

    return sorted(relations.values(), key=lambda relation: relation["item"]["number"])


def find_referenced_prs(issue_number):
    query = f"repo:{REPO} is:pr #{issue_number}"
    found = {}
    for item in search_items(query):
        text = (item.get("title") or "") + "\n" + (item.get("body") or "")
        if re.search(rf"(?<![\w])#\s*{issue_number}\b|/issues/{issue_number}\b", text, re.I):
            found[item["number"]] = item
    return list(found.values())[:10]


def render(issue, relations, prs, source_fingerprint):
    lines = [
        MARKER,
        f"{INPUT_MARKER}{source_fingerprint} -->",
        "## 関連 Issue / 修正 PR（根拠一致のみ）",
        "",
        "> 文章の類似度だけでは関連付けません。Issue は明示的な参照、同一の 32-bit エラー識別子、または「実際の結果」の完全一致が根拠です。共通原因・修正可能性は別途確認が必要です。",
        "",
        "### 同じ症状・明示的な関連 Issue",
    ]
    if relations:
        for relation in relations[:10]:
            item = relation["item"]
            title = (item.get("title") or "(タイトルなし)").replace("[", r"\[").replace("]", r"\]")
            lines.append(f"- [#{item['number']}: {title}]({item.get('html_url', '')})")
            lines.append(f"  - 判定: {relation['kind']}")
            lines.append(f"  - 根拠: {relation['evidence']}")
    else:
        lines.append("- 根拠の強い関連は確認できませんでした。推測で関連付けていません。")

    lines += ["", "### 明示的に Issue を参照する PR"]
    if prs:
        for item in prs:
            title = (item.get("title") or "(タイトルなし)").replace("[", r"\[").replace("]", r"\]")
            state = "merged" if item.get("pull_request", {}).get("merged_at") else item.get("state", "unknown")
            lines.append(f"- [#{item['number']}: {title}]({item.get('html_url', '')}) — {state}")
    else:
        lines.append("- 本 Issue を明示的に参照する PR は検索結果から確認できませんでした。")

    lines += [
        "",
        "### 判定上の制約",
        "- 同じ症状: 強い一致の根拠がある場合のみ表示",
        "- 同じ原因: 自動確定しない",
        "- 同じ修正で直る: PR が Issue を明示参照している場合のみ表示",
        "- Issue のラベル・状態・他の Issue の状態は変更しない",
    ]
    return "\n".join(lines)


def main():
    if not REPO or not CURRENT:
        raise RuntimeError("GITHUB_REPOSITORY and ISSUE_NUMBER are required")
    current = api(f"/repos/{REPO}/issues/{CURRENT}")
    if "pull_request" in current or CURRENT == 178:
        return

    source_fingerprint = fingerprint(current)
    comments = api(f"/repos/{REPO}/issues/{CURRENT}/comments?per_page=100")
    existing = next((
        comment for comment in reversed(comments)
        if MARKER in (comment.get("body") or "")
        and (comment.get("user") or {}).get("login") == "github-actions[bot]"
    ), None)
    if existing and f"{INPUT_MARKER}{source_fingerprint} -->" in (existing.get("body") or ""):
        print(f"Issue #{CURRENT} input is unchanged; reusing the existing relation report.")
        return

    relations = find_issue_relations(current)
    prs = find_referenced_prs(CURRENT)
    body = render(current, relations, prs, source_fingerprint)
    endpoint = (
        f"/repos/{REPO}/issues/comments/{existing['id']}"
        if existing else f"/repos/{REPO}/issues/{CURRENT}/comments"
    )
    api(endpoint, method="PATCH" if existing else "POST", payload={"body": body})
    print(f"Updated evidence-backed report for #{CURRENT}: {len(relations)} issue relations, {len(prs)} explicitly referencing PRs.")


if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as exc:
        print(f"GitHub API error: HTTP {exc.code}", file=sys.stderr)
        if exc.code == 403:
            print("Check Actions permissions and API rate limits.", file=sys.stderr)
        raise
