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
    # Current Issue Forms and legacy free-form Issue headings are both supported.
    "actual": (
        "実際の結果", "実際の動作", "実際の動作・エラーメッセージ",
        "症状", "現象", "事象", "actual result", "actual behavior", "what happened",
    ),
    "logs": (
        "関連ログ・スクリーンショット・エラーメッセージ",
        "実際の動作・エラーメッセージ", "ログ・エラーメッセージ",
        "logs", "error message", "exception",
    ),
    "expected": ("期待する結果", "期待する動作", "expected result", "expected behavior"),
    "steps": ("再現手順", "steps to reproduce", "reproduction steps"),
    "windows": ("windows のバージョン", "windows version"),
    "purge_version": ("purge のバージョン", "purge version"),
    "from_version": ("更新元のバージョン", "update from"),
    "to_version": ("更新先のバージョン", "update to"),
}

# Only a full-width 32-bit hexadecimal error identifier is accepted as standalone evidence.
# Generic exception class names and common MSI codes (for example 1603) are not distinctive enough.
SIGNATURE_PATTERNS = (re.compile(r"\b0x[0-9a-f]{8}\b", re.I),)
REFERENCE_PATTERN = re.compile(r"(?<![\w])#\s*(\d+)\b", re.I)
GITHUB_URL_PATTERN = re.compile(r"https?://github\.com/[^\s<>]+", re.I)


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
    with urllib.request.urlopen(request, timeout=10) as response:
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


def compact_actual(value):
    """Normalize punctuation and spacing for conservative symptom phrase comparison."""
    return re.sub(r"[\W_]+", "", normalize(value), flags=re.UNICODE)


def longest_shared_actual_phrase(left, right, minimum=24, maximum=48):
    """Find an exact, distinctive-length phrase shared by both actual-result fields."""
    left = compact_actual(left)[:500]
    right = compact_actual(right)[:500]
    upper = min(len(left), len(right), maximum)
    for size in range(upper, minimum - 1, -1):
        for start in range(0, len(left) - size + 1):
            phrase = left[start:start + size]
            if phrase in right:
                return phrase
    return None


def actual_search_phrases(value, minimum=24, window=40, limit=4):
    """Create a few targeted phrase queries instead of requiring the whole symptom to match."""
    value = normalize(value)
    if len(value) < minimum:
        return []
    if len(value) <= window:
        return [value]
    offsets = [0, (len(value) - window) // 2, len(value) - window]
    # Add one more overlapping window to tolerate differences near either end.
    offsets.append(min(len(value) - window, max(0, len(value) // 3 - window // 2)))
    phrases = []
    for offset in offsets:
        phrase = value[offset:offset + window].strip()
        if len(phrase) >= minimum and phrase not in phrases:
            phrases.append(phrase)
        if len(phrases) >= limit:
            break
    return phrases


def is_distinctive_phrase(phrase):
    """Reject shared symptom text that becomes generic after removing boilerplate."""
    residual = phrase
    for generic in (
        "nullreferenceexception", "exception", "error", "エラー", "エラーコード",
        "が出ます", "が出る", "が発生します", "が表示されます", "が表示される",
        "発生します", "表示されます", "表示される", "アプリ", "設定画面",
        "起動時", "起動", "失敗", "ファイル", "問題", "できません",
    ):
        residual = residual.replace(compact_actual(generic), "")
    return len(residual) >= 12


def relation_evidence(current, candidate):
    """Return only strong, inspectable evidence; never infer a relation from general similarity."""
    current_fields = parse_fields(current.get("body", ""))
    candidate_fields = parse_fields(candidate.get("body", ""))
    same_signatures = issue_signatures(current) & issue_signatures(candidate)
    if same_signatures:
        return "一致した 32-bit エラー識別子: " + ", ".join(sorted(same_signatures))
    current_actual = normalize(current_fields.get("actual", ""))
    candidate_actual = normalize(candidate_fields.get("actual", ""))
    compact_current = compact_actual(current_actual)
    compact_candidate = compact_actual(candidate_actual)
    if len(compact_current) >= 24 and compact_current == compact_candidate and is_distinctive_phrase(compact_current):
        return "Issueフォームの「実際の結果」が正規化後に完全一致"
    shared_phrase = longest_shared_actual_phrase(current_actual, candidate_actual)
    if shared_phrase and is_distinctive_phrase(shared_phrase):
        return "「実際の結果」に24文字以上の特徴的な症状記述が一致（記号・空白を除外）: " + shared_phrase
    return None


def evidence_search_terms(issue, limit=6):
    """Return a bounded set of distinctive evidence terms for grouped GitHub search."""
    signatures = sorted(issue_signatures(issue))
    fields = parse_fields(issue.get("body", ""))
    phrases = actual_search_phrases(fields.get("actual", ""))
    terms = []
    for term in signatures + phrases:
        term = normalize(term)
        if term and term not in terms:
            terms.append(term)
    return terms[:limit]


def build_evidence_query(kind, term, category=None):
    """Build a GitHub search query for one quoted evidence term."""
    escaped = term.replace("\\", "\\\\").replace('"', '\\"')
    query = f'repo:{REPO} is:{kind} in:title,body "{escaped}"'
    if category:
        query += f' label:"{category}"'
    return query


def build_evidence_queries(kind, terms, category=None):
    """Build at most two bounded searches: category-first, then an unfiltered fallback."""
    selected = []
    for term in terms:
        term = normalize(term)
        if term and term not in selected:
            selected.append(term)
        if len(selected) >= 5:
            break
    if not selected:
        return []

    alternatives = " OR ".join(
        '"' + term.replace("\\", "\\\\").replace('"', '\\"') + '"'
        for term in selected
    )
    base = f"repo:{REPO} is:{kind} in:title,body ({alternatives})"
    queries = []
    if category:
        queries.append(base + f' label:"{category}"')
    queries.append(base)
    return list(dict.fromkeys(queries))[:2]


def pr_relation_evidence(issue, candidate):
    """Only surface a PR without an explicit reference when its title/body shares strong evidence."""
    signatures = issue_signatures(issue)
    candidate_text = (candidate.get("title") or "") + "\n" + (candidate.get("body") or "")
    same_signatures = signatures & extract_signatures(candidate_text)
    if same_signatures:
        return "PR のタイトルまたは本文に同じ 32-bit エラー識別子: " + ", ".join(sorted(same_signatures))
    fields = parse_fields(issue.get("body", ""))
    current_actual = normalize(fields.get("actual", ""))
    candidate_text = normalize(candidate_text)
    shared_phrase = longest_shared_actual_phrase(current_actual, candidate_text)
    if shared_phrase and is_distinctive_phrase(shared_phrase):
        return "Issue の実際の結果と PR のタイトル・本文に24文字以上の特徴的な症状記述が一致: " + shared_phrase
    return None


def reference_targets(text):
    """Return issue numbers referenced locally; ignore URLs to other repositories."""
    value = text or ""
    refs = set()
    repo_parts = REPO.strip("/").split("/", 1)
    expected_repo = tuple(part.casefold() for part in repo_parts) if len(repo_parts) == 2 else None
    for match in GITHUB_URL_PATTERN.finditer(value):
        url = match.group(0).rstrip(".,;:!?)]}")
        parsed = urllib.parse.urlparse(url)
        parts = [part for part in parsed.path.split("/") if part]
        if len(parts) == 4 and parts[2].casefold() == "issues" and parts[3].isdigit():
            if expected_repo and (parts[0].casefold(), parts[1].casefold()) == expected_repo:
                refs.add(int(parts[3]))
    value_without_urls = GITHUB_URL_PATTERN.sub(" ", value)
    refs.update(int(match.group(1)) for match in REFERENCE_PATTERN.finditer(value_without_urls))
    return refs


def extract_issue_refs(text, current_number):
    return {number for number in reference_targets(text) if number != current_number}

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
    relations = {}

    # Explicit references are intentional links and do not require repository-wide search.
    refs = sorted(extract_issue_refs(issue.get("title", "") + "\n" + issue.get("body", ""), current_number))
    if len(refs) > 10:
        print(f"Limiting explicit Issue references from {len(refs)} to 10 to bound API calls.")
    for number in refs[:10]:
        item = direct_issue(number)
        if item:
            relations[number] = {
                "item": item,
                "kind": "明示的な Issue 参照",
                "evidence": f"本文またはタイトルに #{number} の参照があります。",
            }

    # Search up to five distinctive terms in one OR query. If a category label is
    # available, try it first and run one unfiltered fallback only when no strong
    # candidate was found. Evidence validation remains strict after retrieval.
    labels = labels_of(issue)
    category = next((label for label in sorted(CATEGORY_LABELS) if label in labels), None)
    queries = build_evidence_queries("issue", evidence_search_terms(issue), category)
    for query in queries:
        matched_by_search = False
        for item in search_items(query):
            if item.get("number") == current_number or "pull_request" in item:
                continue
            evidence = relation_evidence(issue, item)
            if evidence:
                matched_by_search = True
                relations.setdefault(item["number"], {
                    "item": item,
                    "kind": "同じ症状の強い一致",
                    "evidence": evidence,
                })
        if matched_by_search:
            break

    return sorted(relations.values(), key=lambda relation: relation["item"]["number"])


def find_related_prs(issue):
    current_number = issue["number"]
    found = {}

    # Direct references are the strongest relationship evidence.
    query = f"repo:{REPO} is:pr #{current_number}"
    for item in search_items(query):
        text = (item.get("title") or "") + "\n" + (item.get("body") or "")
        if current_number in reference_targets(text):
            found[item["number"]] = {
                "item": item,
                "kind": "明示的な Issue 参照",
                "evidence": f"PR のタイトルまたは本文に #{current_number} の参照があります。",
            }

    # A PR without a direct reference is only a candidate when the same distinctive
    # error identifier or long symptom phrase appears in its title/body. Search at most
    # five terms in one OR query, then apply the same strict evidence validation.
    for query in build_evidence_queries("pr", evidence_search_terms(issue)):
        for item in search_items(query):
            evidence = pr_relation_evidence(issue, item)
            if evidence:
                found.setdefault(item["number"], {
                    "item": item,
                    "kind": "同じ根拠を含む PR 候補",
                    "evidence": evidence,
                })

    return sorted(found.values(), key=lambda relation: relation["item"]["number"])[:10]


def safe_title(value):
    """Escape untrusted GitHub titles before embedding them in Markdown links."""
    value = " ".join(str(value or "").splitlines()).replace("<", "").replace(">", "")
    special = set(chr(92) + "`*_{}[]()#+.!|")
    return "".join(chr(92) + char if char in special else char for char in value)[:180]

def render(issue, relations, prs, source_fingerprint):
    lines = [
        MARKER,
        f"{INPUT_MARKER}{source_fingerprint} -->",
        "## 関連 Issue / 修正 PR（根拠一致のみ）",
        "",
        "> 文章の類似度だけでは関連付けません。Issue は明示的な参照、同一の 32-bit エラー識別子、または「実際の結果」の完全一致・24文字以上の特徴的な部分一致が根拠です。共通原因・同じ修正で直ることは自動確定しません。",
        "",
        "### 同じ症状・明示的な関連 Issue",
    ]
    if relations:
        for relation in relations[:10]:
            item = relation["item"]
            title = safe_title(item.get("title") or "(タイトルなし)")
            lines.append(f"- [#{item['number']}: {title}]({item.get('html_url', '')})")
            lines.append(f"  - 判定: {relation['kind']}")
            lines.append(f"  - 根拠: {relation['evidence']}")
    else:
        lines.append("- 根拠の強い関連は確認できませんでした。推測で関連付けていません。")

    lines += ["", "### 明示参照または同じ強い根拠を含む PR 候補"]
    if prs:
        for relation in prs:
            item = relation["item"]
            title = safe_title(item.get("title") or "(タイトルなし)")
            state = "merged" if item.get("pull_request", {}).get("merged_at") else item.get("state", "unknown")
            lines.append(f"- [#{item['number']}: {title}]({item.get('html_url', '')}) — {state}")
            lines.append(f"  - 判定: {relation['kind']}")
            lines.append(f"  - 根拠: {relation['evidence']}")
    else:
        lines.append("- 根拠の強い PR 候補は確認できませんでした。")

    lines += [
        "",
        "### 判定上の制約",
        "- 同じ症状: 強い一致の根拠がある場合のみ表示",
        "- 同じ原因・同じ修正で直ること: 自動確定しない",
        "- PR: Issue への明示参照、または同一のエラー識別子・特徴的な症状記述がタイトル/本文にある場合のみ候補表示",
        "- Issue のラベル・状態・他の Issue の状態は変更しない",
        "- GitHub 検索は最大5件の証拠語句を1クエリにまとめ、OR演算子を最大4個に制限",
        "- 分類ラベルがある場合はラベル付き検索を優先し、強い候補がなければラベルなし検索を1回だけ実行",
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
    prs = find_related_prs(current)
    body = render(current, relations, prs, source_fingerprint)
    endpoint = (
        f"/repos/{REPO}/issues/comments/{existing['id']}"
        if existing else f"/repos/{REPO}/issues/{CURRENT}/comments"
    )
    api(endpoint, method="PATCH" if existing else "POST", payload={"body": body})
    print(f"Updated evidence-backed report for #{CURRENT}: {len(relations)} issue relations, {len(prs)} PR candidates.")


if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as exc:
        print(f"GitHub API error: HTTP {exc.code}", file=sys.stderr)
        if exc.code == 403:
            print("Check Actions permissions and API rate limits.", file=sys.stderr)
        raise
