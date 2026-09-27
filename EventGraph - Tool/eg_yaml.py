"""
Unity YAML reading for the Event Graph tool (read-only).

Unity files are several YAML documents, each headed by a tag line such as
    --- !u!114 &11400000
which standard YAML loaders reject. We split on those headers ourselves and
parse each body with a small built-in parser for the subset Unity writes:
block mappings and sequences (sequence items at the same indent as their key),
flow maps/lists ({fileID: 0, guid: ..}, []), plain / single / double quoted
scalars including multi-line ones.

The built-in parser is the default on purpose: Unity writes plain strings such
as "debugText: Placeholder hazard: small rocks ..." that strict YAML parsers
(PyYAML) reject, and it needs no install. 32-character hex GUIDs always stay
strings (a GUID like 0000...e000... is not a number).
"""
import re

HEADER_RE = re.compile(r'^--- !u!(\d+) &(-?\d+)(.*)$', re.M)


def iter_documents(text):
    """Yield (class_id, file_id, body_text) for every document in a Unity file."""
    matches = list(HEADER_RE.finditer(text))
    for i, m in enumerate(matches):
        start = m.end() + 1
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        yield int(m.group(1)), m.group(2), text[start:end]


def parse(body):
    """Parse one document body into Python data (built-in, Unity-tolerant)."""
    return MiniParser(body).parse()


# ---------------------------------------------------------------------------
# Built-in parser (Unity subset)
# ---------------------------------------------------------------------------

_INT_RE = re.compile(r'^[-+]?(0|[1-9]\d*)$')   # leading zeros stay strings (hex data, ids)
_FLOAT_RE = re.compile(r'^[-+]?((\d+\.\d*|\.\d+)([eE][-+]?\d+)?|\d+[eE][-+]?\d+)$')   # needs a '.' or an exponent


_GUID_RE = re.compile(r'^[0-9a-fA-F]{32}$')


def _typed(s):
    s = s.strip()
    if s == '' or s == '~' or s == 'null':
        return None
    if _GUID_RE.match(s):
        return s
    if _INT_RE.match(s):
        try:
            return int(s)
        except ValueError:
            return s
    if _FLOAT_RE.match(s):
        try:
            return float(s)
        except ValueError:
            return s
    if s in ('true', 'True'):
        return True
    if s in ('false', 'False'):
        return False
    return s


class MiniParser:
    def __init__(self, text):
        self.lines = []
        for raw in text.split('\n'):
            if raw.strip() == '' or raw.lstrip().startswith('#'):
                self.lines.append(None)   # keep positions: blank lines matter inside quoted scalars
            else:
                self.lines.append(raw.rstrip('\r'))
        self.raw = text.split('\n')
        self.i = 0

    # -- helpers -----------------------------------------------------------
    @staticmethod
    def _indent(line):
        return len(line) - len(line.lstrip(' '))

    def _skip_blank(self):
        while self.i < len(self.lines) and self.lines[self.i] is None:
            self.i += 1

    def _peek(self):
        self._skip_blank()
        return self.lines[self.i] if self.i < len(self.lines) else None

    # -- structure ---------------------------------------------------------
    def parse(self):
        line = self._peek()
        if line is None:
            return None
        return self._block(self._indent(line))

    def _block(self, indent):
        line = self._peek()
        if line is None:
            return None
        if line.lstrip().startswith('- ') or line.strip() == '-':
            return self._sequence(indent)
        return self._mapping(indent)

    def _mapping(self, indent):
        result = {}
        while True:
            line = self._peek()
            if line is None:
                break
            ind = self._indent(line)
            stripped = line.strip()
            if ind != indent or stripped.startswith('- ') or stripped == '-':
                break
            key, rest = self._split_key(stripped)
            self.i += 1
            result[key] = self._value_after_key(rest, indent)
        return result

    def _sequence(self, indent):
        result = []
        while True:
            line = self._peek()
            if line is None:
                break
            ind = self._indent(line)
            stripped = line.strip()
            if ind != indent or not (stripped.startswith('- ') or stripped == '-'):
                break
            content = stripped[2:] if stripped.startswith('- ') else ''
            self.i += 1
            if content == '':
                nxt = self._peek()
                result.append(self._block(self._indent(nxt)) if nxt is not None and self._indent(nxt) > indent else None)
            elif self._looks_like_key(content):
                # A mapping item: "- key: value" then more keys at indent + 2.
                item_indent = indent + 2
                key, rest = self._split_key(content)
                item = {key: self._value_after_key(rest, item_indent)}
                more = self._peek()
                if more is not None and self._indent(more) == item_indent and not more.strip().startswith('- '):
                    item.update(self._mapping(item_indent))
                result.append(item)
            else:
                result.append(self._scalar(content, indent))
        return result

    def _value_after_key(self, rest, key_indent):
        if rest == '':
            nxt = self._peek()
            if nxt is None:
                return None
            nind = self._indent(nxt)
            if nind > key_indent:
                return self._block(nind)
            if nind == key_indent and (nxt.strip().startswith('- ') or nxt.strip() == '-'):
                return self._sequence(key_indent)   # Unity: list items at the key's indent
            return None
        return self._scalar(rest, key_indent)

    @staticmethod
    def _looks_like_key(s):
        if s[:1] in ('"', "'", '{', '['):
            return False
        return re.match(r'^[^:#]+?:( |$)', s) is not None

    @staticmethod
    def _split_key(s):
        m = re.match(r'^(.+?):(?: (.*))?$', s)
        if not m:
            return s, ''
        return m.group(1).strip(), (m.group(2) or '').strip()

    # -- scalars -----------------------------------------------------------
    def _scalar(self, first, owner_indent):
        if first.startswith('"'):
            return self._double_quoted(first, owner_indent)
        if first.startswith("'"):
            return self._single_quoted(first, owner_indent)
        if first.startswith('{') or first.startswith('['):
            text = first
            while not self._balanced(text) and self.i < len(self.lines):
                nxt = self.lines[self.i]
                self.i += 1
                if nxt is not None:
                    text += ' ' + nxt.strip()
            return self._flow(text)
        # plain scalar: more-indented following lines continue it
        parts = [first]
        while True:
            j = self.i
            while j < len(self.lines) and self.lines[j] is None:
                j += 1
            if j >= len(self.lines) or self._indent(self.lines[j]) <= owner_indent:
                break
            blank = j - self.i
            parts.append('\n' * blank if blank else ' ')
            parts.append(self.lines[j].strip())
            self.i = j + 1
        return _typed(''.join(parts)) if len(parts) == 1 else ''.join(parts).strip()

    def _collect_quoted(self, first, quote):
        """Gather a quoted scalar that may span several lines. Returns the raw inner lines."""
        body = first[1:]
        lines = [body]
        def closed(s):
            if quote == "'":
                k = 0
                while k < len(s):
                    if s[k] == "'":
                        if k + 1 < len(s) and s[k + 1] == "'":
                            k += 2
                            continue
                        return True
                    k += 1
                return False
            k = 0
            while k < len(s):
                if s[k] == '\\':
                    k += 2
                    continue
                if s[k] == '"':
                    return True
                k += 1
            return False
        while not closed(lines[-1]) and self.i < len(self.raw):
            raw = self.raw[self.i]
            self.i += 1
            lines.append(raw.strip())
        return lines

    def _double_quoted(self, first, owner_indent):
        lines = self._collect_quoted(first, '"')
        # fold: single line break -> space, empty line -> \n, trailing "\" joins without space
        out = ''
        for n, ln in enumerate(lines):
            if n == 0:
                out = ln
                continue
            if ln == '':
                out += '\n'
            elif out.endswith('\\') and not out.endswith('\\\\'):
                out = out[:-1] + ln
            elif out.endswith('\n'):
                out += ln
            else:
                out += ' ' + ln
        end = self._closing_index(out, '"')
        inner = out[:end]
        return self._unescape(inner)

    def _single_quoted(self, first, owner_indent):
        lines = self._collect_quoted(first, "'")
        out = ''
        for n, ln in enumerate(lines):
            if n == 0:
                out = ln
            elif ln == '':
                out += '\n'
            elif out.endswith('\n'):
                out += ln
            else:
                out += ' ' + ln
        end = self._closing_index(out, "'")
        return out[:end].replace("''", "'")

    @staticmethod
    def _closing_index(s, quote):
        k = 0
        while k < len(s):
            if quote == '"' and s[k] == '\\':
                k += 2
                continue
            if s[k] == quote:
                if quote == "'" and k + 1 < len(s) and s[k + 1] == "'":
                    k += 2
                    continue
                return k
            k += 1
        return len(s)

    @staticmethod
    def _unescape(s):
        simple = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', '/': '/', '0': '\0', ' ': ' ', 'e': '\x1b', 'a': '\a', 'b': '\b', 'v': '\v', 'f': '\f', 'N': '\x85', '_': '\xa0'}
        out = []
        k = 0
        while k < len(s):
            c = s[k]
            if c == '\\' and k + 1 < len(s):
                n = s[k + 1]
                if n in simple:
                    out.append(simple[n]); k += 2; continue
                if n in 'xuU':
                    width = {'x': 2, 'u': 4, 'U': 8}[n]
                    try:
                        out.append(chr(int(s[k + 2:k + 2 + width], 16))); k += 2 + width; continue
                    except ValueError:
                        pass
            out.append(c)
            k += 1
        return ''.join(out)

    @staticmethod
    def _balanced(s):
        depth = 0
        q = None
        for ch in s:
            if q:
                if ch == q:
                    q = None
                continue
            if ch in '"\'':
                q = ch
            elif ch in '{[':
                depth += 1
            elif ch in '}]':
                depth -= 1
        return depth <= 0

    def _flow(self, s):
        s = s.strip()
        if s.startswith('{'):
            inner = s[1:s.rfind('}')]
            d = {}
            for part in self._split_top(inner):
                if not part.strip():
                    continue
                k, _, v = part.partition(':')
                d[k.strip()] = self._flow_value(v.strip())
            return d
        if s.startswith('['):
            inner = s[1:s.rfind(']')]
            return [self._flow_value(p.strip()) for p in self._split_top(inner) if p.strip()]
        return _typed(s)

    def _flow_value(self, v):
        if v.startswith('{') or v.startswith('['):
            return self._flow(v)
        if v.startswith('"'):
            return self._unescape(v[1:self._closing_index(v[1:], '"') + 1])
        if v.startswith("'"):
            return v[1:-1].replace("''", "'")
        return _typed(v)

    @staticmethod
    def _split_top(s):
        parts, depth, cur, q = [], 0, '', None
        for ch in s:
            if q:
                cur += ch
                if ch == q:
                    q = None
                continue
            if ch in '"\'':
                q = ch
            elif ch in '{[':
                depth += 1
            elif ch in '}]':
                depth -= 1
            if ch == ',' and depth == 0:
                parts.append(cur)
                cur = ''
            else:
                cur += ch
        parts.append(cur)
        return parts
