#!/usr/bin/env python3
"""Diff source, renderer and coverage check for a PR review story document.

  pr_story.py files <PR>                           classify files, number hunks, list new/changed/removed tests
                                                   and unchanged uses
  pr_story.py diff <PR>                            print the diff the story is built from
  pr_story.py show <PR> <path> <n>                 print hunk n with numbered lines, for excerpts
  pr_story.py render <PR> <template.md> <doc.md>   expand placeholders into verbatim code blocks and links
  pr_story.py check <PR> <doc.md>                  report production lines, tests, files and unchanged uses
                                                   missing from the doc
  pr_story.py post <PR> <doc.md> [--dry-run]       post the doc as PR comments, split at headings to fit
                                                   GitHub's limit, updating this story's earlier comments

<PR> is a GitHub PR number or `git:<range>` such as `git:master...HEAD`. A PR is fetched into
refs/pr-story/<N> and diffed locally with the histogram algorithm, which splits rewrites into
more readable hunks than `gh pr diff`; `gh pr diff` is the fallback when the fetch fails.

Block placeholders, each alone on its line, expand to a link line and one fenced block:
  {{hunk:<path>:<n>}}             hunk n (as numbered by `files`)
  {{hunk:<path>:<n>,<m>}}         several hunks of one file in one block
  {{hunk:<path>:<n>@<a>-<b>}}     lines a..b of hunk n (as numbered by `show`), marked as an excerpt
  {{file:<path>}}                 every hunk of the file; a whole new file renders as plain source
  {{code:<path>:<a>-<b>}}         unchanged source lines a..b of the file at the PR head
  {{code@base:<path>:<a>-<b>}}    the same from the base revision
  append `|nodoc` to a hunk or file placeholder to collapse each run of `///` lines into one `/// ...` line
Inline placeholder, anywhere in a line:
  {{link:<path>:<a>}} or {{link:<path>:<a>-<b>}}   a link to those lines at the PR head commit

An unchanged use is an unchanged line in a changed method whose name binds to a different declaration
than at base, such as a parameter that became an out variable of a newly extracted method. Its text is
unchanged but what it refers to is not.
"""
import re
import subprocess
import sys

BULK_PATTERNS = [
    r"\.verified\.(txt|json|cs)$", r"\.received\.", r"\.snap$", r"__snapshots__/",
    r"(^|/)(package-lock\.json|yarn\.lock|pnpm-lock\.yaml|packages\.lock\.json|Cargo\.lock|poetry\.lock|go\.sum)$",
    r"\.g\.cs$", r"\.g\.i\.cs$", r"\.Designer\.cs$", r"(^|/)(Generated|generated|gen)/", r"\.min\.(js|css)$",
    r"\.(png|jpg|jpeg|gif|ico|svg|pdf|zip|dll|exe|woff2?)$",
]
TEST_PATTERNS = [
    r"(^|/)[^/]*\.Tests?/", r"(^|/)tests?/", r"(^|/)__tests__/", r"(^|/)spec/",
    r"Tests?\.cs$", r"\.(test|spec)\.[jt]sx?$", r"_test\.(go|py)$", r"(^|/)test_[^/]*\.py$",
]
SUPPORT_PATTERNS = [r"(^|/)[^/]*(Benchmark|Sample|Example)s?[^/]*/"]
DOC_PATTERNS = [r"\.(md|mdx|rst|adoc)$", r"(^|/)docs?/"]
DATA_PATTERNS = [r"\.(json|ya?ml|csv|xml|txt)$"]

TEST_ATTRIBUTE = re.compile(r"^\s*\[(?:Fact|Theory|Test|TestCase|TestMethod|DataTestMethod)\b")
ATTRIBUTE_OR_BLANK = re.compile(r"^\s*(\[.*)?$")
CSHARP_METHOD = re.compile(
    r"^\s*(?:(?:public|private|internal|protected|static|async|override|virtual)\s+)+[\w<>\[\],.? ]+?\s+(\w+)\s*\(")
OTHER_TEST_METHODS = [
    re.compile(r"^\s*(?:async\s+)?def\s+(test\w*)\s*\("),
    re.compile(r"^\s*func\s+(Test\w*)\s*\("),
    re.compile(r"""^\s*(?:it|test)\s*\(\s*['"`]([^'"`]+)['"`]"""),
]
NOT_A_TYPE = {"return", "new", "throw", "await", "case", "is", "as", "in", "else", "yield", "goto", "using",
              "when", "and", "or", "not", "ref", "params", "this", "base", "typeof", "nameof", "default", "lock"}
DECLARATION = re.compile(r"\b(out\s+)?([A-Za-z_][\w.]*(?:<[^<>()]*>)?(?:\[\])?\??)\s+([a-z_]\w*)\s*(?=[=,);])")
LANGUAGES = {"cs": "csharp", "py": "python", "ts": "typescript", "tsx": "tsx", "js": "javascript", "go": "go",
             "java": "java", "kt": "kotlin", "rs": "rust", "razor": "razor", "css": "css", "sql": "sql",
             "sh": "bash", "md": "markdown", "json": "json", "xml": "xml", "yml": "yaml", "yaml": "yaml"}
HUNK_PLACEHOLDER = re.compile(r"^\{\{(hunk|file):([^:}|]+)(?::([\d,@-]+))?(\|nodoc)?\}\}$")
CODE_PLACEHOLDER = re.compile(r"^\{\{code(@base)?:([^:}|]+):(\d+)-(\d+)\}\}$")
LINK_PLACEHOLDER = re.compile(r"\{\{link:([^:}|]+):(\d+)(?:-(\d+))?\}\}")
HUNK_HEADER = re.compile(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})(.*)$")


class Source:
    """The diff of one PR or range, the revisions on both sides, and the blob URL of the head commit."""

    def __init__(self, diff_text, head, base, blob_url):
        self.diff_text = diff_text
        self.head = head  # a revision, None for the working tree, False when unknown
        self.base = base
        self.blob_url = blob_url  # https://github.com/<owner>/<repo>/blob/<sha>, or None
        self.files = parse(diff_text)

    def link(self, path, first, last=None):
        lines = f"{first}-{last}" if last and last != first else f"{first}"
        if not self.blob_url:
            return f"`{path}:{lines}`"
        anchor = f"#L{first}-L{last}" if last and last != first else f"#L{first}"
        return f"[`{path}:{lines}`]({self.blob_url}/{path}{anchor})"


def classify(path):
    for category, patterns in (("bulk", BULK_PATTERNS), ("test", TEST_PATTERNS),
                               ("support", SUPPORT_PATTERNS), ("docs", DOC_PATTERNS), ("data", DATA_PATTERNS)):
        if any(re.search(pattern, path) for pattern in patterns):
            return category
    return "production"


def run(command):
    return subprocess.run(command, check=True, capture_output=True, text=True).stdout


def try_run(command):
    try:
        return run(command).strip()
    except (subprocess.CalledProcessError, OSError):
        return ""


def git_diff(diff_range):
    return run(["git", "diff", "--no-color", "--no-ext-diff", "--diff-algorithm=histogram", "-U3", diff_range])


def blob_url(head):
    """A GitHub blob URL for a head commit that GitHub has, or None."""
    sha = try_run(["git", "rev-parse", head]) if head else ""
    repository = try_run(["gh", "repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"])
    return f"https://github.com/{repository}/blob/{sha}" if sha and repository else None


def resolve(pr):
    if pr.startswith("git:"):
        diff_range = pr[4:]
        parts = re.split(r"\.\.\.?", diff_range)
        head = parts[1] if len(parts) > 1 and parts[1] else None
        base = parts[0] or "HEAD"
        if "..." in diff_range:
            base = run(["git", "merge-base", base, head or "HEAD"]).strip()
        pushed = head and try_run(["git", "branch", "-r", "--contains", head])
        return Source(git_diff(diff_range), head, base, blob_url(head) if pushed else None)
    try:
        base = run(["gh", "pr", "view", pr, "--json", "baseRefOid", "-q", ".baseRefOid"]).strip()
        head = f"refs/pr-story/{pr}"
        run(["git", "fetch", "-q", "origin", f"+pull/{pr}/head:{head}", base])
        merge_base = run(["git", "merge-base", base, head]).strip()
        return Source(git_diff(f"{base}...{head}"), head, merge_base, blob_url(head))
    except subprocess.CalledProcessError:
        return Source(run(["gh", "pr", "diff", pr, "--color=never"]), False, False, None)


def read_revision(revision, path):
    """The file's lines at a revision (None: working tree), or [] when unavailable."""
    if revision is False:
        return []
    try:
        if revision is None:
            with open(path, encoding="utf-8") as handle:
                return handle.read().splitlines()
        return run(["git", "show", f"{revision}:{path}"]).splitlines()
    except (subprocess.CalledProcessError, OSError):
        return []


def parse(diff_text):
    """Returns {path: [hunk]}; a hunk is {header, new_start, lines: [(sign, text)]}."""
    files, path, hunk = {}, None, None
    for line in diff_text.splitlines():
        if line.startswith("diff --git "):
            path = line.split(" b/", 1)[1]
            files[path] = []
            hunk = None
        elif line.startswith("@@") and path is not None:
            match = HUNK_HEADER.match(line)
            hunk = {"header": line, "new_start": int(match.group(3)) if match else 1, "lines": []}
            files[path].append(hunk)
        elif hunk is None:
            continue  # file header lines (index, ---, +++, mode changes)
        elif line[:1] in "+- ":
            hunk["lines"].append((line[0], line[1:]))
    return files


def new_line_numbers(hunk):
    """The head line number at each hunk line; a removed line gets the number of the next head line."""
    numbers, line_number = [], hunk["new_start"]
    for sign, _ in hunk["lines"]:
        numbers.append(line_number)
        if sign != "-":
            line_number += 1
    return numbers


def method_starts(lines):
    """(index, name, is_test) for each method signature in a list of source lines."""
    starts = []
    for index, text in enumerate(lines):
        match = CSHARP_METHOD.search(text)
        if match:
            back = index - 1
            while back >= 0 and ATTRIBUTE_OR_BLANK.match(lines[back]) and not TEST_ATTRIBUTE.search(lines[back]):
                back -= 1
            starts.append((index, match.group(1), back >= 0 and bool(TEST_ATTRIBUTE.search(lines[back]))))
            continue
        for pattern in OTHER_TEST_METHODS:
            match = pattern.search(text)
            if match:
                starts.append((index, match.group(1), True))
    return starts


def side_lines(hunks, sign):
    """The lines of one side of the diff, with a blank line between hunks."""
    lines = []
    for hunk in hunks:
        lines += [text for line_sign, text in hunk["lines"] if line_sign in (sign, " ")] + [""]
    return lines


def signature_touched(hunks, sign, name):
    pattern = re.compile(rf"\b{re.escape(name)}\s*\(|['\"`]{re.escape(name)}['\"`]")
    return any(line_sign == sign and pattern.search(text) for hunk in hunks for line_sign, text in hunk["lines"])


def test_changes(path, hunks, head):
    """Returns (new, changed, removed) test names for one test file."""
    after = [name for _, name, is_test in method_starts(side_lines(hunks, "+")) if is_test]
    before = [name for _, name, is_test in method_starts(side_lines(hunks, "-")) if is_test]
    new = [name for name in dict.fromkeys(after) if signature_touched(hunks, "+", name) and name not in before]
    removed = [name for name in dict.fromkeys(before) if signature_touched(hunks, "-", name) and name not in after]
    changed = [name for name in dict.fromkeys(after) if name in before
               and (signature_touched(hunks, "+", name) or signature_touched(hunks, "-", name))]
    starts = method_starts(read_revision(head, path))
    for hunk in hunks:
        for (sign, text), line_number in zip(hunk["lines"], new_line_numbers(hunk)):
            line_index = line_number - 1
            if sign == "-" and CSHARP_METHOD.search(text):
                continue  # a removed or renamed signature, reported as removed rather than as a change above it
            if sign == " " or not text.strip():
                continue
            if ATTRIBUTE_OR_BLANK.match(text):
                # An attribute belongs to the method below it, e.g. an added [InlineData] case.
                owner = next((start for start in starts if start[0] >= line_index), None)
            else:
                enclosing = [start for start in starts
                             if start[0] < line_index or (sign == "+" and start[0] == line_index)]
                owner = enclosing[-1] if enclosing else None
            if owner and owner[2] and owner[1] not in new and owner[1] not in changed:
                changed.append(owner[1])
    return new, changed, removed


def method_span(starts, line_number):
    """(first, last) head or base line numbers of the method containing a line, or None."""
    for i in range(len(starts) - 1):
        if starts[i] <= line_number < starts[i + 1]:
            return starts[i], starts[i + 1] - 1
    return None


def declaration_forms(content, span, name):
    forms = set()
    for line_number in range(span[0], span[1] + 1):
        for out, type_name, found in DECLARATION.findall(content[line_number - 1].split("//", 1)[0]):
            if found == name and type_name not in NOT_A_TYPE:
                forms.add(f"{out.strip()} {type_name}".strip())
    return forms


def unchanged_uses(path, hunks, source):
    """(line number, name, text) for unchanged head lines whose name binds to a different declaration than at base.

    Candidates are names declared on changed lines, checked in the methods the PR changes. A line binds
    differently when its enclosing method declares the name differently at head than the method that held
    the same line at base, for example a parameter that became an out variable of a newly extracted method.
    """
    head_content, base_content = read_revision(source.head, path), read_revision(source.base, path)
    if not head_content or not base_content:
        return []
    head_starts = [start[0] + 1 for start in method_starts(head_content)] + [len(head_content) + 1]
    base_starts = [start[0] + 1 for start in method_starts(base_content)] + [len(base_content) + 1]
    names, shown, changed_spans, offsets = set(), set(), set(), []
    for hunk in hunks:
        match = HUNK_HEADER.match(hunk["header"])
        old_start, old_count = int(match.group(1)), int(match.group(2) or 1)
        new_start, new_count = int(match.group(3)), int(match.group(4) or 1)
        offsets.append((new_start + new_count, old_count - new_count))
        for (sign, text), line_number in zip(hunk["lines"], new_line_numbers(hunk)):
            if sign != "-":
                shown.add(line_number)
            if sign != " ":
                span = method_span(head_starts, line_number)
                if span:
                    changed_spans.add(span)
                names.update(found for _, type_name, found in DECLARATION.findall(text.split("//", 1)[0])
                             if type_name not in NOT_A_TYPE)
    uses = []
    for span in sorted(changed_spans):
        for line_number in range(span[0], span[1] + 1):
            if line_number in shown:
                continue
            code = head_content[line_number - 1].split("//", 1)[0]
            base_line = line_number + sum(offset for end, offset in offsets if end <= line_number)
            base_span = method_span(base_starts, base_line)
            if not base_span or normalized(base_content[base_line - 1]) != normalized(head_content[line_number - 1]):
                continue
            rebound = [name for name in sorted(names)
                       if re.search(rf"(?<![\w.]){re.escape(name)}\b", code)
                       and not any(found == name for _, _, found in DECLARATION.findall(code))
                       and declaration_forms(head_content, span, name)
                       != declaration_forms(base_content, base_span, name)]
            if rebound:
                uses.append((line_number, ", ".join(rebound), head_content[line_number - 1].strip()))
    return uses


def significant(text):
    stripped = text.strip()
    return len(stripped) > 2 and not stripped.startswith(("//", "#", "*", "/*"))


def normalized(text):
    return " ".join(text.split())


def collapse_docs(lines):
    """Replaces each run of same-sign `///` lines with one `/// ...` line."""
    result = []
    for sign, text in lines:
        if text.lstrip().startswith("///"):
            marker = (sign, text[:len(text) - len(text.lstrip())] + "/// ...")
            if not result or result[-1] != marker:
                result.append(marker)
        else:
            result.append((sign, text))
    return result


def fenced(language, body):
    """A code block whose fence is longer than any backtick run inside it."""
    longest = max([len(backticks) for line in body for backticks in re.findall(r"`{3,}", line)] or [2])
    fence = "`" * (longest + 1)
    return [f"{fence}{language}"] + body + [fence]


def render_block(source, path, selections, nodoc):
    """selections: [(hunk, first, last)] with 1-based inclusive line bounds within the hunk."""
    def lines_of(hunk, first, last):
        chosen = hunk["lines"][first - 1:last]
        return collapse_docs(chosen) if nodoc else chosen

    head_lines = [number for hunk, first, last in selections
                  for (sign, _), number in zip(hunk["lines"][first - 1:last], new_line_numbers(hunk)[first - 1:last])
                  if sign != "-"]
    label = [source.link(path, min(head_lines), max(head_lines)) if head_lines else f"`{path}`"]
    whole_hunks = all((first, last) == (1, len(hunk["lines"])) for hunk, first, last in selections)
    if whole_hunks and all(hunk["header"].startswith("@@ -0,0 ") for hunk, _, _ in selections):
        language = LANGUAGES.get(path.rsplit(".", 1)[-1], "")
        return label + fenced(language, [text for hunk, first, last in selections
                                         for _, text in lines_of(hunk, first, last)])
    body = []
    for hunk, first, last in selections:
        excerpt = (first, last) != (1, len(hunk["lines"]))
        body.append(hunk["header"] + (f" (excerpt: lines {first}-{last} of this hunk)" if excerpt else ""))
        body += [sign + text for sign, text in lines_of(hunk, first, last)]
    return label + fenced("diff", body)


def command_files(pr):
    source = resolve(pr)
    totals, uses = {}, []
    for path, hunks in source.files.items():
        category = classify(path)
        added = sum(1 for hunk in hunks for sign, _ in hunk["lines"] if sign == "+")
        removed = sum(1 for hunk in hunks for sign, _ in hunk["lines"] if sign == "-")
        totals[category] = totals.get(category, 0) + 1
        print(f"{category:<10} +{added:<5} -{removed:<5} hunks={len(hunks):<3} {path}")
        if category == "production":
            if len(hunks) > 1:
                for index, hunk in enumerate(hunks, 1):
                    print(f"{'':<10} hunk {index}: {hunk['header'][:110]}")
            uses += [(path, use) for use in unchanged_uses(path, hunks, source)]
        if category == "test":
            new, changed, gone = test_changes(path, hunks, source.head)
            content = read_revision(source.head, path)
            starts = method_starts(content)
            ranges = {name: f"{start + 1}-{(starts[i + 1][0] if i + 1 < len(starts) else len(content))}"
                      for i, (start, name, _) in enumerate(starts)}
            for label, names in (("new test", new), ("changed test", changed), ("removed test", gone)):
                for name in names:
                    print(f"{'':<10} {label}: {name}" + (f"  (head lines {ranges[name]})" if name in ranges else ""))
    print("\n" + ", ".join(f"{count} {category}" for category, count in sorted(totals.items())))
    print("Category is a path heuristic: reclassify by reading the file when it looks wrong.")
    if uses:
        print("\nUnchanged uses (unchanged lines whose [names] bind to a different declaration than at base):")
        for path, (line_number, name, text) in uses:
            print(f"  {path}:{line_number}  [{name}]  {text[:100]}")
    print(f"\nLinks: {source.blob_url or 'none, the head commit is not on GitHub'}")


def command_show(pr, path, index):
    source = resolve(pr)
    hunk = source.files[path][int(index) - 1]
    print(hunk["header"])
    for number, ((sign, text), line_number) in enumerate(zip(hunk["lines"], new_line_numbers(hunk)), 1):
        print(f"{number:>4} {line_number:>5} {sign}{text}")


def command_render(pr, template_path, doc_path):
    source = resolve(pr)
    files = source.files
    with open(template_path, encoding="utf-8") as handle:
        template = handle.read().splitlines()
    output, used, errors = [], {}, []

    def replace_link(match):
        path, first, last = match.group(1), int(match.group(2)), int(match.group(3) or match.group(2))
        if not 1 <= first <= last <= len(read_revision(source.head, path)):
            errors.append(f"link {path}:{first}-{last} is outside the file at head")
        return source.link(path, first, last)

    for number, line in enumerate(template, 1):
        code_match = CODE_PLACEHOLDER.match(line.strip())
        if code_match:
            from_base, path = code_match.group(1), code_match.group(2)
            first, last = int(code_match.group(3)), int(code_match.group(4))
            content = read_revision(source.base if from_base else source.head, path)
            if not 1 <= first <= last <= len(content):
                errors.append(f"line {number}: {path} has {len(content)} lines at that revision, got {first}-{last}")
                continue
            label = f"`{path}:{first}-{last}` at base" if from_base else source.link(path, first, last)
            output.append(f"{label} (unchanged)")
            output += fenced(LANGUAGES.get(path.rsplit(".", 1)[-1], ""), content[first - 1:last])
            continue
        match = HUNK_PLACEHOLDER.match(line.strip())
        if not match:
            output.append(LINK_PLACEHOLDER.sub(replace_link, line))
            continue
        kind, path, spec, nodoc = match.groups()
        if path not in files:
            errors.append(f"line {number}: unknown path {path}")
            continue
        hunks = files[path]
        parts = [str(index) for index in range(1, len(hunks) + 1)] if kind == "file" else (spec or "").split(",")
        selections = []
        for part in filter(None, parts):
            index_text, _, bounds = part.partition("@")
            index = int(index_text)
            if not 1 <= index <= len(hunks):
                errors.append(f"line {number}: {path} has {len(hunks)} hunks, got {index}")
                continue
            hunk = hunks[index - 1]
            first, last = (int(bound) for bound in bounds.split("-")) if bounds else (1, len(hunk["lines"]))
            if not 1 <= first <= last <= len(hunk["lines"]):
                errors.append(f"line {number}: hunk {index} of {path} has {len(hunk['lines'])} lines, got {bounds}")
                continue
            selections.append((hunk, first, last))
            for line_index in range(first, last + 1):
                used.setdefault((path, index, line_index), []).append(number)
        if selections:
            output += render_block(source, path, selections, bool(nodoc))
    for (path, index, line_index), lines in used.items():
        if len(lines) > 1 and files[path][index - 1]["lines"][line_index - 1][0] != " ":
            errors.append(f"{path} hunk {index} line {line_index} shown on template lines {lines}")
    with open(doc_path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(output) + "\n")
    for path, hunks in files.items():
        if classify(path) != "production":
            continue
        for index, hunk in enumerate(hunks, 1):
            unused = [line_index for line_index, (sign, _) in enumerate(hunk["lines"], 1)
                      if sign != " " and (path, index, line_index) not in used]
            if unused:
                print(f"UNUSED       {path} hunk {index} lines {unused[0]}-{unused[-1]} ({len(unused)} changed)")
    for error in errors:
        print(f"ERROR        {error}")
    print(f"wrote {doc_path}")
    if errors:
        sys.exit(1)


def shown_lines(doc_text):
    """Changed lines shown in code blocks, keyed by sign; non-diff blocks count as added lines."""
    found, fence = set(), None
    for line in doc_text.splitlines():
        match = FENCE.match(line)
        if fence is None:
            if match:
                fence = (match.group(1), match.group(2).strip())
            continue
        if match and match.group(1)[0] == fence[0][0] and len(match.group(1)) >= len(fence[0]) \
                and not match.group(2).strip():
            fence = None
        elif fence[1] == "diff":
            if line[:1] in "+-":
                found.add((line[0], normalized(line[1:])))
        elif fence[1] not in ("mermaid", "text"):  # diagrams and pseudo code never show production code
            found.add(("+", normalized(line)))
    return found


def referenced(doc_text, path, line_number):
    """True when the doc names path:line or a path:a-b range containing it, as text or link."""
    for match in re.finditer(rf"{re.escape(path)}:(\d+)(?:-(\d+))?", doc_text):
        if int(match.group(1)) <= line_number <= int(match.group(2) or match.group(1)):
            return True
    return False


def command_check(pr, doc_path):
    source = resolve(pr)
    with open(doc_path, encoding="utf-8") as handle:
        doc_text = handle.read()
    shown = shown_lines(doc_text)
    missing_hunks, missing_tests, missing_files, missing_uses = [], [], [], []
    for path, hunks in source.files.items():
        category = classify(path)
        if category == "production":
            for hunk in hunks:
                changed = [(sign, text) for sign, text in hunk["lines"] if sign != " "]
                required = [(sign, text) for sign, text in changed if significant(text)]
                absent = [(sign, text) for sign, text in required if (sign, normalized(text)) not in shown]
                silently_dropped = changed and not required and hunk["header"] not in doc_text and not any(
                    (sign, normalized(text)) in shown for sign, text in changed)
                if absent or silently_dropped:
                    missing_hunks.append((path, hunk["header"], absent or changed))
            for line_number, name, text in unchanged_uses(path, hunks, source):
                if not referenced(doc_text, path, line_number):
                    missing_uses.append(f"{path}:{line_number} [{name}] {text[:90]}")
            continue
        if path.rsplit("/", 1)[-1].split(".")[0] not in doc_text:
            missing_files.append(f"{category}: {path}")
        if category == "test":
            new, changed, gone = test_changes(path, hunks, source.head)
            missing_tests += [f"{path}: {name}" for name in new + changed + gone if name not in doc_text]

    for path, header, absent in missing_hunks:
        print(f"MISSING HUNK {path} {header}")
        for sign, text in absent[:5]:
            print(f"    {sign}{text}")
        if len(absent) > 5:
            print(f"    ... {len(absent) - 5} more lines")
    for entry in missing_tests:
        print(f"MISSING TEST {entry}")
    for entry in missing_files:
        print(f"UNMENTIONED  {entry}")
    for entry in missing_uses:
        print(f"UNSHOWN USE  {entry}")
    if missing_hunks or missing_tests or missing_files or missing_uses:
        print(f"\nFAIL: {len(missing_hunks)} hunks, {len(missing_tests)} tests, {len(missing_files)} files, "
              f"{len(missing_uses)} unchanged uses")
        sys.exit(1)
    print("OK: every production line is shown, every new, changed and removed test is named, "
          "every other file is mentioned, every unchanged use is shown or linked")


COMMENT_LIMIT = 60000  # GitHub allows 65,536 characters; the rest is headroom for the part marker
COMMENT_MARKER = "<!-- pr-review-story part "


def split_for_comments(text):
    """Splits at `## ` headings outside code blocks into parts below COMMENT_LIMIT characters."""
    sections, current, fence = [], [], None
    for line in text.splitlines():
        match = FENCE.match(line)
        if fence is None and match:
            fence = match.group(1)
        elif fence is not None and match and line.strip().startswith(fence) and not match.group(2).strip():
            fence = None
        if fence is None and line.startswith("## ") and current:
            sections.append("\n".join(current))
            current = []
        current.append(line)
    sections.append("\n".join(current))
    parts, part = [], ""
    for section in sections:
        if len(section) > COMMENT_LIMIT:
            raise SystemExit(f"a section is {len(section)} characters, above the {COMMENT_LIMIT} limit: "
                             f"split it into smaller chapters ({section.splitlines()[0][:80]})")
        if part and len(part) + len(section) + 1 > COMMENT_LIMIT:
            parts.append(part)
            part = ""
        part = f"{part}\n{section}" if part else section
    return parts + [part]


def command_post(pr, doc_path, *options):
    with open(doc_path, encoding="utf-8") as handle:
        parts = split_for_comments(handle.read())
    bodies = [f"{COMMENT_MARKER}{index}/{len(parts)} -->\n{part}" for index, part in enumerate(parts, 1)]
    if "--dry-run" in options:
        for index, body in enumerate(bodies, 1):
            print(f"part {index}/{len(bodies)}: {len(body)} characters, starts with {body.splitlines()[1][:70]}")
        return
    repository = run(["gh", "repo", "view", "--json", "nameWithOwner", "-q", ".nameWithOwner"]).strip()
    login = run(["gh", "api", "user", "-q", ".login"]).strip()
    existing = run(["gh", "api", "--paginate", f"repos/{repository}/issues/{pr}/comments", "-q",
                    f'.[] | select(.user.login == "{login}" and (.body | startswith("{COMMENT_MARKER}"))) | .id'])
    comment_ids = existing.split()
    for index, body in enumerate(bodies):
        if index < len(comment_ids):
            run(["gh", "api", "-X", "PATCH", f"repos/{repository}/issues/comments/{comment_ids[index]}",
                 "-f", f"body={body}", "-q", ".html_url"])
            print(f"updated part {index + 1}: comment {comment_ids[index]}")
        else:
            url = run(["gh", "api", f"repos/{repository}/issues/{pr}/comments", "-f", f"body={body}",
                       "-q", ".html_url"]).strip()
            print(f"posted part {index + 1}: {url}")
    for comment_id in comment_ids[len(bodies):]:
        run(["gh", "api", "-X", "PATCH", f"repos/{repository}/issues/comments/{comment_id}", "-f",
             f"body={COMMENT_MARKER}unused -->\n_The review story now has fewer parts; this one is no longer used._"])
        print(f"emptied leftover comment {comment_id}")


if __name__ == "__main__":
    arguments = sys.argv[1:]
    commands = {("files", 2): command_files, ("diff", 2): lambda pr: sys.stdout.write(resolve(pr).diff_text),
                ("show", 4): command_show, ("render", 4): command_render, ("check", 3): command_check,
                ("post", 3): command_post, ("post", 4): command_post}
    command = commands.get((arguments[0], len(arguments)) if arguments else None)
    if command is None:
        print(__doc__)
        sys.exit(2)
    command(*arguments[1:])
