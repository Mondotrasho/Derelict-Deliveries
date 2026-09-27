"""
Builds the event graph from a project Snapshot. Pure data, no GUI.

Nodes: events, tables (with the sources that roll them), planet options,
quest start events, dialogues, enemies and endings. Edges are what the data
says happens next: table/option/start -> event, choice outcome -> follow-up /
scheduled event / dialogue / enemy / ending, and (optionally shown) flag links
from the event that writes a flag to the event or option that requires it.

It only describes the data. Warnings are facts about the data (a missing
reference, a flag nobody sets), never suggestions.
"""
import os
import re

from eg_project import guid_of

CATEGORY = {0: 'Asteroid', 1: 'Planet', 2: 'Derelict', 3: 'Hazard', 4: 'Pickup'}
RESOURCE = {0: 'Fuel', 1: 'Hull', 2: 'Crew', 3: 'Supplies', 4: 'Shields', 5: 'Damage'}
SCOPE = {0: 'Player', 1: 'Planet', 2: 'Site'}
COMPARE = {0: '>=', 1: '<=', 2: '=='}
POI_KIND = {0: 'Dialogue', 1: 'Combat', 2: 'Event'}
ENDING_RE = re.compile(r'EVENT END\s*(\d+)\s*:\s*([^\n]+)', re.I)

# Keys the game's code writes (so "required but never set" doesn't fire for them).
STORY_MAX_WRITERS = 3             # a flag set by more events than this is "shared state", not a story link
STORY_MAX_READERS = 5
CODE_WRITTEN_PREFIXES = ('events.resolved.', 'poi:', 'event:available', 'res.supplies',
                         'officer:', 'bonus:')   # OfficerRoster mirrors officers into these


def _num(v, default=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return default


def _int(v, default=0):
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return default


def _s(v):
    return '' if v is None else str(v)


class Node:
    def __init__(self, nid, kind, title, subtitle='', category=None, path=None, data=None):
        self.id = nid
        self.kind = kind            # event | table | option | start | dialogue | enemy | ending
        self.title = title
        self.subtitle = subtitle
        self.category = category
        self.path = path
        self.data = data or {}
        self.guid = None
        self.group = None           # id of the flow this node belongs to (see compute_groups)


class Edge:
    def __init__(self, src, dst, kind, label='', detail=''):
        self.src = src
        self.dst = dst
        self.kind = kind            # entry | follow | schedule | dialogue | spawn | combat | ending | flag
        self.label = label
        self.detail = detail


class Issue:
    def __init__(self, level, text, node_id=None):
        self.level = level          # error | warning | info
        self.text = text
        self.node_id = node_id


class StateKey:
    """One flag / tag / counter, and every place that writes or reads it."""
    def __init__(self, kind, scope, key):
        self.kind = kind            # flag | tag | counter
        self.scope = scope
        self.key = key
        self.writers = []           # (node_id, where, value)  value: True/False for flags & tags, None for counters
        self.readers = []           # (node_id, where, value)

    @property
    def label(self):
        return f'{self.kind} {self.key} ({self.scope})'


class Graph:
    def __init__(self):
        self.groups = {}            # group id -> {'name': str, 'nodes': [ids], 'named_by': str}
        self.nodes = {}
        self.edges = []
        self.issues = []
        self.state = {}             # (kind, scope, key) -> StateKey
        self.path_nodes = {}        # file path -> set(node ids)

    def add(self, node):
        self.nodes[node.id] = node
        if node.path:
            self.path_nodes.setdefault(os.path.normcase(node.path), set()).add(node.id)
        return node

    def edge(self, *a, **k):
        e = Edge(*a, **k)
        self.edges.append(e)
        return e

    def issue(self, level, text, node_id=None):
        self.issues.append(Issue(level, text, node_id))

    def key(self, kind, scope, key):
        k = (kind, scope, key)
        if k not in self.state:
            self.state[k] = StateKey(kind, scope, key)
        return self.state[k]


# --------------------------------------------------------------------------
# Build
# --------------------------------------------------------------------------

def build(snap, project):
    g = Graph()
    events = {guid: a for guid, a in snap.assets.items() if a.cls == 'EventDefinition'}
    tables = {guid: a for guid, a in snap.assets.items() if a.cls == 'EventTable'}
    enemies = {guid: a for guid, a in snap.assets.items() if a.cls == 'EnemyShipDefinition'}

    for path, msg in snap.parse_errors:
        g.issue('error', f'{os.path.basename(path)}: {msg}')

    def ref_name(guid):
        p = snap.guid_to_path.get(guid)
        return os.path.basename(p) if p else f'missing {guid[:8]}...'

    def check_ref(guid, owner, what, expect=None):
        """True if the reference resolves (and is the expected kind)."""
        if not guid or guid == '0' * 32:
            return False
        if guid not in snap.guid_to_path:
            g.issue('error', f'{owner_title(owner)}: {what} points to a missing asset ({guid[:8]}...)', owner)
            return False
        if expect == 'event' and guid not in events:
            g.issue('warning', f'{owner_title(owner)}: {what} is {ref_name(guid)}, not an EventDefinition', owner)
            return False
        if expect == 'enemy' and guid not in enemies:
            g.issue('warning', f'{owner_title(owner)}: {what} is {ref_name(guid)}, not an EnemyShipDefinition', owner)
            return False
        return True

    def owner_title(nid):
        n = g.nodes.get(nid)
        return n.title if n else nid

    def ensure_dialogue(guid):
        nid = 'dl:' + guid
        if nid not in g.nodes:
            path = snap.guid_to_path.get(guid)
            n = g.add(Node(nid, 'dialogue', os.path.basename(path) if path else 'missing dialogue', 'dialogue', path=path))
            n.guid = guid
            data = project.load_dialogue(snap, guid)
            n.data = data or {}
            check_dialogue(g, n)
        return nid

    def ensure_enemy(guid):
        nid = 'en:' + guid
        if nid not in g.nodes:
            a = enemies.get(guid)
            title = _s(a.data.get('displayName')) or a.name if a else ref_name(guid)
            n = g.add(Node(nid, 'enemy', title, 'enemy', path=a.path if a else None, data=a.data if a else {}))
            n.guid = guid
        return nid

    def ensure_ending(label, number):
        nid = 'end:' + label.lower()
        if nid not in g.nodes:
            g.add(Node(nid, 'ending', f'END {number}' if number else 'END', label.title()))
        return nid

    # -- events --------------------------------------------------------
    ids_seen = {}
    for guid, a in events.items():
        d = a.data
        cat = CATEGORY.get(_int(d.get('category'), -1), '?')
        title = _s(d.get('displayName')) or a.name
        n = g.add(Node('ev:' + guid, 'event', title, _s(d.get('id')) or a.name, cat, a.path, d))
        n.guid = guid
        eid = _s(d.get('id'))
        if eid:
            if eid in ids_seen:
                g.issue('error', f'Event id "{eid}" is used by both {os.path.basename(ids_seen[eid])} and {os.path.basename(a.path)}', n.id)
            ids_seen[eid] = a.path

    for guid, a in events.items():
        nid = 'ev:' + guid
        d = a.data
        read_conditions(g, d.get('conditions'), nid, 'event conditions')
        write_state(g, d.get('onResolve'), nid, 'on resolve')
        for res in d.get('pickupResources') or []:
            if _int(res.get('kind'), -1) == 3:
                g.key('counter', 'Player', 'res.supplies').writers.append((nid, 'pickup', None))

        choices = d.get('choices') or []
        seen_choice = set()
        for c in choices:
            cid = _s(c.get('id'))
            ctext = _s(c.get('text')) or cid
            if cid in seen_choice:
                g.issue('warning', f'{g.nodes[nid].title}: two choices share the id "{cid}" (only the first can be picked)', nid)
            seen_choice.add(cid)
            read_conditions(g, c.get('availability'), nid, f'choice "{ctext}"')
            if _int(c.get('minCrew')) > 0:
                pass  # crew is a resource, not state

            dlg = guid_of(c.get('dialogue'))
            if dlg and check_ref(dlg, nid, f'choice "{ctext}" dialogue'):
                g.edge(nid, ensure_dialogue(dlg), 'dialogue', ctext)

            outs = c.get('outcomes') or []
            if not outs:
                g.issue('warning', f'{g.nodes[nid].title}: choice "{ctext}" has no outcomes', nid)
                continue
            total = sum(max(0.0, _num(o.get('weight'), 0)) for o in outs)
            if total <= 0:
                g.issue('warning', f'{g.nodes[nid].title}: choice "{ctext}" has outcome weights that add up to 0', nid)
            for o in outs:
                w = max(0.0, _num(o.get('weight'), 0))
                pct = f' ({w / total:.0%})' if total > 0 and len(outs) > 1 else ''
                label = ctext + pct
                where = f'choice "{ctext}" / {(_s(o.get("label")) or "outcome")}'
                write_state(g, o.get('writes'), nid, where)
                for res in o.get('resources') or []:
                    if _int(res.get('kind'), -1) == 3:
                        g.key('counter', 'Player', 'res.supplies').writers.append((nid, where, None))

                fu = guid_of(o.get('followUp'))
                if fu and check_ref(fu, nid, f'{where} follow-up', 'event'):
                    g.edge(nid, 'ev:' + fu, 'follow', label)
                sc = guid_of(o.get('scheduleEvent'))
                if sc and check_ref(sc, nid, f'{where} scheduled event', 'event'):
                    turns = max(1, _int(o.get('scheduleInTurns'), 1))
                    g.edge(nid, 'ev:' + sc, 'schedule', f'{label} · +{turns} turn{"s" if turns != 1 else ""}')
                sp = guid_of(o.get('spawnOnMapEdge'))
                if sp and check_ref(sp, nid, f'{where} spawn', 'enemy'):
                    g.edge(nid, ensure_enemy(sp), 'spawn', f'{label} · spawns at map edge')
                cb = guid_of(o.get('startCombatWith'))
                if cb and check_ref(cb, nid, f'{where} combat', 'enemy'):
                    g.edge(nid, ensure_enemy(cb), 'combat', f'{label} · combat')
                m = ENDING_RE.search(_s(o.get('resultText')))
                if m:
                    g.edge(nid, ensure_ending(m.group(2).strip(), m.group(1)), 'ending', label)

    # -- tables and the sources that roll them ------------------------------
    sources_by_table = {}
    for s in snap.sources:
        sources_by_table.setdefault(s['table'], []).append(s)
    for guid, a in tables.items():
        nid = 'tb:' + guid
        srcs = sources_by_table.get(guid, [])
        sub = ', '.join(s['label'] + ('' if s['enabled'] else ' (disabled)') for s in srcs) or 'not used by any source'
        n = g.add(Node(nid, 'table', a.name, sub, path=a.path, data=a.data))
        n.guid = guid
        if not srcs:
            g.issue('info', f'{a.name} is not used by any source or hazard controller in the scene', nid)
        entries = a.data.get('entries') or []
        total = sum(max(0.0, _num(e.get('weight'), 0)) for e in entries)
        src_tags = {t for s in srcs for t in s['tags']}
        for e in entries:
            eg = guid_of(e.get('definition'))
            w = max(0.0, _num(e.get('weight'), 0))
            if not eg:
                g.issue('warning', f'{a.name} has an empty entry', nid)
                continue
            if not check_ref(eg, nid, 'entry', 'event'):
                continue
            if w <= 0:
                g.issue('info', f'{a.name}: {g.nodes["ev:" + eg].title} has weight 0 (never picked)', nid)
            g.edge(nid, 'ev:' + eg, 'entry', f'w {w:g}' + (f' ({w / total:.0%})' if total > 0 else ''))
            ev_tags = {str(t) for t in (events[eg].data.get('tags') or [])}
            if src_tags and not (ev_tags & src_tags):
                g.issue('warning', f'{a.name}: {g.nodes["ev:" + eg].title} has tags {sorted(ev_tags)} but its source only rolls {sorted(src_tags)} (never picked)', 'ev:' + eg)
    for s in snap.sources:
        if s['table'] not in tables:
            g.issue('error' if s['table'] not in snap.guid_to_path else 'warning',
                    f'{s["label"]}: its table {ref_name(s["table"])} is not an EventTable in this project')

    # -- planet options -----------------------------------------------------
    poi = snap.poi or {}
    planet_names = {p['id']: (p['displayName'] or p['id']) for p in snap.planets}
    for p in snap.planets:
        for t in p['tags']:
            g.key('tag', 'Planet', t).writers.append((None, f'planet {p["id"]}', True))

    def add_option(opt, planet_id=None):
        oid = _s(opt.get('id')) or _s(opt.get('title'))
        nid = f'op:{planet_id or "*"}:{oid}'
        kind = POI_KIND.get(_int(opt.get('kind'), -1), '?')
        where = planet_names.get(planet_id, planet_id) if planet_id else planet_requirement(opt) or 'any planet'
        n = g.add(Node(nid, 'option', _s(opt.get('title')) or oid, f'{where} · {kind}', data=opt))
        read_conditions(g, opt.get('conditions'), nid, 'option conditions')
        write_state(g, opt.get('onComplete'), nid, 'on complete')
        if kind == 'Event':
            ev = guid_of(opt.get('eventDefinition'))
            if ev and check_ref(ev, nid, 'event', 'event'):
                g.edge(nid, 'ev:' + ev, 'entry', 'option')
            elif not ev:
                g.issue('warning', f'Planet option "{n.title}" is an Event option with no Event Definition', nid)
        elif kind == 'Dialogue':
            dj = guid_of(opt.get('dialogueJson'))
            if dj and check_ref(dj, nid, 'dialogue'):
                g.edge(nid, ensure_dialogue(dj), 'dialogue', 'hail')
        return n

    for entry in poi.get('planetOptions') or []:
        for opt in entry.get('options') or []:
            add_option(opt, _s(entry.get('planetId')))
    for opt in poi.get('sharedOptions') or []:
        add_option(opt)

    # -- quest start events ------------------------------------------------
    sched = snap.scheduler or {}
    for i, st in enumerate(sched.get('startEvents') or []):
        ev = guid_of(st.get('definition'))
        nid = f'st:{i}'
        g.add(Node(nid, 'start', 'Quest start', f'from turn {_int(st.get("fromTurn"), 1)}', data=st))
        if ev and check_ref(ev, nid, 'start event', 'event'):
            g.edge(nid, 'ev:' + ev, 'entry', f'turn >= {_int(st.get("fromTurn"), 1)}')

    # -- run summary lines count as readers ---------------------------------
    warp = snap.warp or {}
    for line in warp.get('summaryLines') or []:
        k = {0: 'counter', 1: 'flag', 2: 'tag'}.get(_int(line.get('kind'), -1))
        if k and line.get('key'):
            g.key(k, 'Player', _s(line.get('key'))).readers.append((None, f'run summary "{_s(line.get("label"))}"', None))

    # -- flag links + state checks -------------------------------------------
    for sk in g.state.values():
        # A link means "this makes that possible": only where the value written is the value
        # required (setting cult:passengers OFF does not unlock something that needs it ON).
        pairs = set()
        for w, _, wv in sk.writers:
            for r, _, rv in sk.readers:
                if w and r and w != r and (wv is None or rv is None or wv == rv):
                    pairs.add((w, r))
        # A key only a few events touch is a story link ("this leads to that"): cult:egg,
        # MiningJobsDone. Busy shared keys (res.supplies) stay plain flag links.
        # Count only the events that can actually unlock something (setting a flag off
        # never does), so cult:passengers stays a story link however many stages clear it.
        writers = {w for w, _ in pairs}
        readers = {r for _, r in pairs}
        story = (not sk.key.startswith(CODE_WRITTEN_PREFIXES) and sk.scope != 'Site'
                 and len(writers) <= STORY_MAX_WRITERS and len(readers) <= STORY_MAX_READERS)
        for w, r in sorted(pairs):
            g.edge(w, r, 'story' if story else 'flag', sk.key, sk.label)
        code = sk.key.startswith(CODE_WRITTEN_PREFIXES)
        if sk.readers and not sk.writers and not code and sk.scope != 'Site':
            for r, where, _ in sk.readers:
                g.issue('warning', f'{sk.label} is required by {owner_title(r) if r else "?"} ({where}) but nothing sets it', r)
        only_planet_data = all(w is None for w, _, _ in sk.writers)   # e.g. colour tags used by the Banner Library
        if sk.writers and not sk.readers and sk.scope != 'Site' and not only_planet_data:
            w0 = next((w for w, _, _ in sk.writers if w), None)
            g.issue('info', f'{sk.label} is set by {owner_title(w0) if w0 else "planet data"} but nothing checks it', w0)
        if sk.scope == 'Site' and sk.readers and not sk.writers:
            for r, where, _ in sk.readers:
                g.issue('warning', f'{sk.label} is required by {owner_title(r)} ({where}) but nothing on that site sets it', r)

    # -- reachability ---------------------------------------------------------
    incoming = {e.dst for e in g.edges if e.kind in ('entry', 'follow', 'schedule')}
    for nid, n in g.nodes.items():
        if n.kind == 'event' and nid not in incoming:
            g.issue('warning', f'{n.title} ({n.subtitle}) is not reachable: no table, planet option, start event, follow-up or schedule leads to it', nid)
        if n.kind == 'event' and not (n.data.get('choices') or []) and n.category not in ('Pickup',):
            g.issue('info', f'{n.title} has no choices (shows the placeholder Resolve / Leave buttons)', nid)

    # drop edges to nodes that don't exist (missing refs already reported)
    g.edges = [e for e in g.edges if e.src in g.nodes and e.dst in g.nodes]
    compute_groups(g, snap)
    resolve_banners(g, snap)
    return g


# --------------------------------------------------------------------------
# Banners (mirrors BannerLibrary.Resolve / ResolvePlanet in the game)
# --------------------------------------------------------------------------

SPRITE_FILEID = 21300000        # a single-sprite texture's sprite


def _ref(ref):
    """(guid, fileID) of a Unity object reference, or (None, None)."""
    g_ = guid_of(ref)
    if not g_ or g_ == '0' * 32:
        return None, None
    return g_, (ref.get('fileID') if isinstance(ref, dict) else None)


def resolve_banners(g, snap):
    """Sets node.banner on events and planet options:
       {'single': (guid, fileID, source)} or {'planets': [(planet, guid, fileID, source)]}.
    Also records g.banner_uses: guid -> [(node id or None, why)] and banner checks."""
    libs = [a for a in snap.assets.values() if a.cls == 'BannerLibrary']
    g.banner_library = libs[0] if libs else None
    g.banner_uses = {}
    lib = g.banner_library.data if g.banner_library else {}
    if len(libs) > 1:
        g.issue('info', f'{len(libs)} BannerLibrary assets found; showing {libs[0].name} (the event UI uses the one assigned to it)')
    if not libs:
        g.issue('info', 'No BannerLibrary asset found: only events with their own Banner field show a banner')

    def use(gid, nid, why):
        if gid:
            g.banner_uses.setdefault(gid, []).append((nid, why))

    def exists(gid):
        return gid in snap.guid_to_path

    # library slots
    tag_entries = []
    for e in lib.get('planetTags') or []:
        gid, fid = _ref(e.get('banner'))
        tag_entries.append((_s(e.get('tag')).strip(), gid, fid))
    asteroid = _ref(lib.get('asteroid'))
    derelict = _ref(lib.get('derelict'))
    fallback = _ref(lib.get('planetFallback'))
    overrides = {}
    for e in lib.get('definitionOverrides') or []:
        dg, _ = _ref(e.get('definition'))
        if dg:
            overrides[dg] = _ref(e.get('banner'))
    for label, (gid, _) in (('Asteroid default', asteroid), ('Derelict default', derelict), ('Planet fallback', fallback)):
        use(gid, None, f'library: {label}')
    for tag, gid, _ in tag_entries:
        use(gid, None, f'library: planet tag {tag}')
    for label, (gid, _) in (('Asteroid default', asteroid), ('Derelict default', derelict), ('Planet fallback', fallback)):
        if gid and not exists(gid):
            g.issue('error', f'Banner Library: {label} points to a missing image')
    planet_tags = {t.lower() for p in snap.planets for t in p['tags']}
    for tag, gid, _ in tag_entries:
        if gid and not exists(gid):
            g.issue('error', f'Banner Library: planet tag "{tag}" points to a missing image')
        if not gid:
            g.issue('warning', f'Banner Library: planet tag "{tag}" has no banner set')
        if tag and tag.lower() not in planet_tags:
            g.issue('info', f'Banner Library: planet tag "{tag}" is not on any planet in the scene')

    def planet_banner(planet):
        tags = {t.strip().lower() for t in planet['tags']}
        for tag, gid, fid in tag_entries:
            if gid and tag.lower() in tags:
                return gid, fid, f'planet tag {tag}'
        if fallback[0]:
            return fallback[0], fallback[1], 'planet fallback'
        return None, None, 'no banner'

    g.planet_banners = {}
    for p in snap.planets:
        gid, fid, why = planet_banner(p)
        g.planet_banners[p['id']] = (gid, fid, why)
        use(gid, None, f'planet {p["id"]}')
        if not gid:
            g.issue('warning', f'Planet {p["id"]} shows no banner: none of its tags {p["tags"]} is in the Banner Library and there is no fallback')

    def planets_for(requirements):
        """Planets matching any of the requirement sets [(planet_id or None, [(tag, must)])]."""
        out = []
        for p in snap.planets:
            tags = {t.strip().lower() for t in p['tags']}
            for pid, conds in requirements:
                if pid and pid != p['id']:
                    continue
                if all((t.lower() in tags) == must for t, must in conds):
                    out.append(p)
                    break
        return out

    def planet_conds(cond):
        return [(_s(t.get('tag')).strip(), bool(_int(t.get('mustHave'), 1)))
                for t in ((cond or {}).get('tags') or []) if _int(t.get('scope')) == 1]

    option_req = {}
    for nid, n in g.nodes.items():
        if n.kind == 'option':
            pid = nid.split(':', 2)[1]
            option_req[nid] = (None if pid == '*' else pid, planet_conds(n.data.get('conditions')))
            ps = planets_for([option_req[nid]])
            n.banner = {'planets': [(p['id'],) + planet_banner(p) for p in ps]}
            for p in ps:
                use(planet_banner(p)[0], nid, f'planet {p["id"]}')

    for nid, n in g.nodes.items():
        if n.kind != 'event':
            continue
        own = _ref(n.data.get('banner'))
        if own[0]:
            n.banner = {'single': (own[0], own[1], "the event's own Banner field")}
        elif n.guid in overrides and overrides[n.guid][0]:
            o = overrides[n.guid]
            n.banner = {'single': (o[0], o[1], 'Banner Library per-event override')}
        elif n.category in ('Asteroid', 'Hazard'):
            n.banner = {'single': asteroid + ('Banner Library asteroid default',)}
        elif n.category == 'Derelict':
            n.banner = {'single': derelict + ('Banner Library derelict default',)}
        elif n.category == 'Planet':
            reqs = [option_req[e.src] for e in g.edges if e.dst == nid and e.src in option_req]
            ev_conds = planet_conds(n.data.get('conditions'))
            if reqs:
                reqs = [(pid, conds + ev_conds) for pid, conds in reqs]
            else:
                reqs = [(None, ev_conds)]
            ps = planets_for(reqs)
            n.banner = {'planets': [(p['id'],) + planet_banner(p) for p in ps]}
        else:
            n.banner = {'single': (None, None, 'no banner for this category')}
        if 'single' in n.banner:
            gid, fid, why = n.banner['single']
            use(gid, nid, why)
            if gid and not exists(gid):
                g.issue('error', f'{n.title}: banner ({why}) points to a missing image', nid)
            if not gid and n.category in ('Asteroid', 'Hazard', 'Derelict') and n.data.get('choices'):
                g.issue('info', f'{n.title} shows no banner: the Banner Library\'s {n.category.lower() if n.category != "Hazard" else "asteroid"} default is empty', nid)
        else:
            for pl, gid, fid, why in n.banner['planets']:
                use(gid, nid, f'on {pl}')


def compute_groups(g, snap):
    """Cluster nodes into flows (quests, chains) and name them.

    1. Events, planet options and quest starts that are joined by follow-ups,
       schedules, endings, story links or option/start entries form one flow.
       A tag 'group:<Name>' on events forces them into the same flow and names it.
    2. A table joins the flow of events it rolls when those events have no flow
       of their own yet (plain asteroid / derelict content), but never glues two
       bigger flows together.
    3. Dialogues and enemies can be shared (the eldritch monster), so they sit in
       the flow that links to them most rather than merging flows.
    Names: a 'group:' tag, else a tag shared by 2+ events that isn't a source tag,
    else the table's name, else the first event's title."""
    parent = {nid: nid for nid in g.nodes}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)

    source_tags = {t for s in snap.sources for t in s['tags']} | {'hazard', 'pickup', 'mining', 'derelict', 'planet'}

    def tags_of(nid):
        n = g.nodes[nid]
        return [str(t) for t in (n.data.get('tags') or [])] if n.kind == 'event' else []

    # forced groups
    forced = {}
    for nid in g.nodes:
        for t in tags_of(nid):
            if t.lower().startswith('group:'):
                name = t[6:].strip()
                if name in forced:
                    union(forced[name], nid)
                else:
                    forced[name] = nid

    joiners = ('follow', 'schedule', 'ending', 'story')
    for e in g.edges:
        a, b = g.nodes[e.src], g.nodes[e.dst]
        if e.kind in joiners and a.kind in ('event', 'option') and b.kind in ('event', 'option', 'ending'):
            union(e.src, e.dst)
        elif e.kind == 'entry' and a.kind in ('option', 'start'):
            union(e.src, e.dst)

    def members():
        m = {}
        for nid in g.nodes:
            m.setdefault(find(nid), []).append(nid)
        return m

    def is_named(root, m):
        evs = [x for x in m[root] if g.nodes[x].kind == 'event']
        if any(t.lower().startswith('group:') for x in evs for t in tags_of(x)):
            return True
        counts = {}
        for x in evs:
            for t in set(tags_of(x)) - source_tags:
                counts[t] = counts.get(t, 0) + 1
        return any(c >= 2 for c in counts.values())

    # Tables join plain content. Each table goes to the flow where most of that
    # flow's events come from it (asteroid table -> mining chain, derelict table ->
    # the wreck); a flow takes at most one table; lone events always follow their table.
    m = members()
    rolled = {}
    for e in g.edges:
        if e.kind == 'entry' and g.nodes[e.src].kind == 'table':
            rolled.setdefault(e.src, set()).add(e.dst)
    pairs = []
    for tnid, evs in rolled.items():
        for r in {find(x) for x in evs}:
            if is_named(r, m):
                continue
            group_events = [x for x in m[r] if g.nodes[x].kind == 'event']
            share = len([x for x in group_events if x in evs]) / max(1, len(group_events))
            pairs.append((share, len(group_events), tnid, r))
    taken_tables, taken_groups = set(), set()
    for share, size, tnid, r in sorted(pairs, key=lambda p: (-p[0], -p[1], p[2], p[3])):
        if size == 1 and len(m[r]) == 1:
            union(tnid, r)                     # a lone event always sits with its table
            continue
        if tnid in taken_tables or r in taken_groups:
            continue
        taken_tables.add(tnid)
        taken_groups.add(r)
        union(tnid, r)
    m = members()

    # satellites: dialogues and enemies go to the flow that links to them most
    for nid, n in g.nodes.items():
        if n.kind not in ('dialogue', 'enemy'):
            continue
        votes = {}
        for e in g.edges:
            other = e.src if e.dst == nid else e.dst if e.src == nid else None
            if other and g.nodes[other].kind not in ('dialogue', 'enemy'):
                r = find(other)
                votes[r] = votes.get(r, 0) + 1
        if votes:
            best = sorted(votes.items(), key=lambda kv: (-kv[1], kv[0]))[0][0]
            parent[find(nid)] = best

    # loose planet options (Hail, Provoke...) share one group
    m = members()
    loose = [ids[0] for ids in m.values() if len(ids) == 1 and g.nodes[ids[0]].kind == 'option']
    for x in loose[1:]:
        union(loose[0], x)

    m = members()
    g.groups = {}
    for root, ids in m.items():
        evs = [x for x in ids if g.nodes[x].kind == 'event']
        name, why = None, ''
        for x in evs:
            for t in tags_of(x):
                if t.lower().startswith('group:'):
                    name, why = t[6:].strip(), 'group tag'
        if not name:
            counts = {}
            for x in evs:
                for t in set(tags_of(x)) - source_tags:
                    counts[t] = counts.get(t, 0) + 1
            shared = sorted(((c, t) for t, c in counts.items() if c >= 2), reverse=True)
            if shared:
                name, why = shared[0][1].replace('_', ' ').title(), f'shared tag "{shared[0][1]}"'
        if not name:
            tables = [x for x in ids if g.nodes[x].kind == 'table']
            if tables:
                tname = g.nodes[tables[0]].title
                name, why = re.sub(r'EventTable$', '', tname).strip() + ' events', 'table'
        if not name and len(ids) > 1 and all(g.nodes[x].kind == 'option' for x in ids):
            name, why = 'Planet options', 'loose planet options'
        if not name:
            # the flow's entry: an event nothing inside the flow leads to
            inside = set(ids)
            led_to = {e.dst for e in g.edges if e.src in inside and e.dst in inside
                      and e.kind in ('follow', 'schedule', 'story')}
            entries = [x for x in evs if x not in led_to] or evs or ids
            first = sorted(entries, key=lambda x: g.nodes[x].title.lower())[0]
            name, why = g.nodes[first].title, 'entry event'
        gid = 'g:' + root
        g.groups[gid] = {'name': name, 'nodes': ids, 'named_by': why}
        for x in ids:
            g.nodes[x].group = gid


def planet_requirement(opt):
    tags = [t for t in ((opt.get('conditions') or {}).get('tags') or []) if _int(t.get('scope')) == 1 and _int(t.get('mustHave'), 1)]
    return ', '.join(f'{_s(t.get("tag"))} planet' for t in tags)


def read_conditions(g, cond, nid, where):
    if not isinstance(cond, dict):
        return
    for t in cond.get('tags') or []:
        g.key('tag', SCOPE.get(_int(t.get('scope')), '?'), _s(t.get('tag'))).readers.append((nid, where, bool(_int(t.get('mustHave'), 1))))
    for f in cond.get('flags') or []:
        g.key('flag', SCOPE.get(_int(f.get('scope')), '?'), _s(f.get('key'))).readers.append((nid, where, bool(_int(f.get('expected'), 1))))
    for c in cond.get('counters') or []:
        g.key('counter', SCOPE.get(_int(c.get('scope')), '?'), _s(c.get('key'))).readers.append((nid, where, None))


def write_state(g, writes, nid, where):
    if not isinstance(writes, dict):
        return
    for t in writes.get('tags') or []:
        g.key('tag', SCOPE.get(_int(t.get('scope')), '?'), _s(t.get('tag'))).writers.append((nid, where, bool(_int(t.get('mustHave'), 1))))
    for f in writes.get('flags') or []:
        g.key('flag', SCOPE.get(_int(f.get('scope')), '?'), _s(f.get('key'))).writers.append((nid, where, bool(_int(f.get('expected'), 1))))
    for c in writes.get('counters') or []:
        g.key('counter', SCOPE.get(_int(c.get('scope')), '?'), _s(c.get('key'))).writers.append((nid, where, None))


def check_dialogue(g, n):
    d = n.data
    if not d:
        g.issue('error', f'Dialogue {n.title}: file missing or empty', n.id)
        return
    if '__error__' in d:
        g.issue('error', f'Dialogue {n.title}: not valid JSON ({d["__error__"]})', n.id)
        return
    lines = d.get('lines') or []
    ticks = {l.get('tick') for l in lines}
    chars = d.get('characters') or {}
    speakers = {'left', 'right', _s(chars.get('left')).lower(), _s(chars.get('right')).lower()}
    for l in lines:
        sp = _s(l.get('speaker')).lower()
        if sp not in speakers:
            g.issue('warning', f'Dialogue {n.title}: tick {l.get("tick")} speaker "{l.get("speaker")}" is neither the left nor right character (the line is skipped)', n.id)
        nt = l.get('nextTick') or 0
        if nt and nt not in ticks:
            g.issue('warning', f'Dialogue {n.title}: tick {l.get("tick")} jumps to missing tick {nt}', n.id)
    for cs in d.get('choices') or []:
        if cs.get('tick') not in ticks:
            g.issue('warning', f'Dialogue {n.title}: choices at tick {cs.get("tick")}, which has no lines', n.id)
        for o in cs.get('options') or []:
            nt = o.get('nextTick') or 0
            if nt and nt not in ticks:
                g.issue('warning', f'Dialogue {n.title}: option "{o.get("text")}" jumps to missing tick {nt}', n.id)


# --------------------------------------------------------------------------
# Views
# --------------------------------------------------------------------------

PRIMARY = ('entry', 'follow', 'schedule', 'dialogue', 'spawn', 'combat', 'ending', 'story')


def reachable(g, start, forward=True, kinds=PRIMARY, flag_prefix=''):
    """Nodes reachable from start. Flag links are followed only if 'flag' is in
    kinds, and then only for keys starting with flag_prefix (so shared counters
    like res.supplies don't connect everything to everything)."""
    seen, stack = {start}, [start]
    while stack:
        cur = stack.pop()
        for e in g.edges:
            if e.kind not in kinds:
                continue
            if e.kind == 'flag' and flag_prefix and not e.label.startswith(flag_prefix):
                continue
            a, b = (e.src, e.dst) if forward else (e.dst, e.src)
            if a == cur and b not in seen:
                seen.add(b)
                stack.append(b)
    return seen


def to_mermaid(g, node_ids, edge_kinds):
    def mid(nid):
        return 'n' + re.sub(r'[^A-Za-z0-9]', '_', nid)

    def esc(s):
        return s.replace('"', "'")

    lines = ['flowchart LR']
    shape = {'event': ('["', '"]'), 'table': ('[/"', '"/]'), 'option': ('(["', '"])'),
             'start': ('(("', '"))'), 'dialogue': ('[["', '"]]'), 'enemy': ('{{"', '"}}'), 'ending': ('(["', '"])')}
    by_group = {}
    for nid in node_ids:
        by_group.setdefault(g.nodes[nid].group, []).append(nid)
    for gid, ids in by_group.items():
        info = g.groups.get(gid)
        boxed = info is not None and len(ids) > 1
        if boxed:
            lines.append(f'  subgraph {mid(gid)}["{esc(info["name"])}"]')
        for nid in ids:
            n = g.nodes[nid]
            a, b = shape.get(n.kind, ('["', '"]'))
            lines.append(f'  {"  " if boxed else ""}{mid(nid)}{a}{esc(n.title)}<br/><small>{esc(n.subtitle)}</small>{b}')
        if boxed:
            lines.append('  end')
    arrow = {'entry': '-->', 'follow': '-->', 'schedule': '-.->', 'dialogue': '-->', 'spawn': '==>', 'combat': '==>',
             'ending': '-->', 'story': '-.->', 'flag': '-.->'}
    for e in g.edges:
        if e.kind in edge_kinds and e.src in node_ids and e.dst in node_ids:
            lab = esc(e.label)[:60]
            lines.append(f'  {mid(e.src)} {arrow.get(e.kind, "-->")}|"{lab}"| {mid(e.dst)}')
    return '\n'.join(lines) + '\n'


def details(g, nid, project=None, snap=None):
    """Rich-text description for the side panel: list of (text, tag)."""
    n = g.nodes.get(nid)
    if not n:
        return [('Nothing selected.', 'dim')]
    out = []
    add = lambda t, tag='': out.append((t, tag))
    add(n.title + '\n', 'h1')
    add(f'{n.kind}' + (f' · {n.category}' if n.category else '') + (f' · {n.subtitle}' if n.subtitle else '') + '\n', 'dim')
    if n.path:
        add(os.path.relpath(n.path, project.root) if project else n.path, 'path')
        add('\n')
    grp = g.groups.get(n.group)
    if grp and len(grp['nodes']) > 1:
        add('Flow: ', 'b'); add(f'{grp["name"]} ({len(grp["nodes"])} nodes, named by {grp["named_by"]})\n', 'dim')
    add('\n')
    banner = getattr(n, 'banner', None)
    if banner:
        if 'single' in banner:
            gid, fid, why = banner['single']
            if gid:
                add(f'{why}\n', 'img:' + gid + '|' + str(fid or '') + '|360')
            else:
                add(f'Banner: none ({why})\n', 'dim')
        else:
            ps = banner['planets']
            if not ps:
                add('Banner: depends on the planet, but no planet in the scene matches its conditions\n', 'warn')
            else:
                add('Banner (the planet\'s own, by tag):\n', 'b')
                for pl, gid, fid, why in ps:
                    if gid:
                        add(f'{pl}: {why}\n', 'img:' + gid + '|' + str(fid or '') + '|200')
                    else:
                        add(f'  {pl}: no banner ({why})\n', 'warn')
        add('\n')
    d = n.data

    def cond_text(cond):
        parts = []
        if not isinstance(cond, dict):
            return ''
        for t in cond.get('tags') or []:
            parts.append(f'{SCOPE.get(_int(t.get("scope")), "?")} tag {"has" if _int(t.get("mustHave"), 1) else "lacks"} {_s(t.get("tag"))}')
        for f in cond.get('flags') or []:
            parts.append(f'{SCOPE.get(_int(f.get("scope")), "?")} flag {_s(f.get("key"))} = {"on" if _int(f.get("expected"), 1) else "off"}')
        for c in cond.get('counters') or []:
            parts.append(f'{SCOPE.get(_int(c.get("scope")), "?")} {_s(c.get("key"))} {COMPARE.get(_int(c.get("compare")), "?")} {_s(c.get("value"))}')
        return '; '.join(parts)

    def writes_text(w):
        parts = []
        if not isinstance(w, dict):
            return ''
        for t in w.get('tags') or []:
            parts.append(f'{"+" if _int(t.get("mustHave"), 1) else "-"}tag {_s(t.get("tag"))} ({SCOPE.get(_int(t.get("scope")), "?")})')
        for f in w.get('flags') or []:
            parts.append(f'flag {_s(f.get("key"))} = {"on" if _int(f.get("expected"), 1) else "off"} ({SCOPE.get(_int(f.get("scope")), "?")})')
        for c in w.get('counters') or []:
            parts.append(f'{_s(c.get("key"))} += {_s(c.get("value"))} ({SCOPE.get(_int(c.get("scope")), "?")})')
        return '; '.join(parts)

    def ref_title(ref):
        gid = guid_of(ref)
        if not gid or gid == '0' * 32:
            return None
        for prefix in ('ev:', 'en:', 'dl:'):
            if prefix + gid in g.nodes:
                return g.nodes[prefix + gid].title
        if snap is not None and gid in snap.guid_to_path:
            return os.path.splitext(os.path.basename(snap.guid_to_path[gid]))[0]
        return 'missing asset'

    if n.kind == 'event':
        tags = ', '.join(_s(t) for t in (d.get('tags') or []))
        if tags:
            add('Tags: ', 'b'); add(tags + '\n')
        ct = cond_text(d.get('conditions'))
        if ct:
            add('Appears when: ', 'b'); add(ct + '\n')
        wt = writes_text(d.get('onResolve'))
        if wt:
            add('On resolve: ', 'b'); add(wt + '\n')
        desc = _s(d.get('shortDescription')) or _s(d.get('debugText'))
        if desc:
            add('\n' + desc + '\n', 'quote')
        if not _int(d.get('allowLeave'), 1) or n.category == 'Hazard':
            add('No "Leave for later".\n', 'dim')
        pr = d.get('pickupResources') or []
        if pr:
            add('Pickup: ', 'b'); add(', '.join(f'{RESOURCE.get(_int(r.get("kind")), "?")} {_num(r.get("amount")):+g}' for r in pr) + '\n')
        for c in d.get('choices') or []:
            add('\n▸ ' + (_s(c.get('text')) or _s(c.get('id'))), 'h2')
            flags = []
            if _int(c.get('minCrew')) > 0:
                flags.append(f'needs {_int(c.get("minCrew"))} crew')
            ct = cond_text(c.get('availability'))
            if ct:
                flags.append('only if ' + ct)
            if _s(c.get('bonusStat')):
                flags.append(f'[{_s(c.get("bonusStat")).title()}] +{_num(c.get("chancePerPoint"), 0.05):.0%} per point'
                             + (f', needs {_int(c.get("minBonus"))}' if _int(c.get('minBonus')) else ''))
            if not _int(c.get('endsEvent'), 1):
                flags.append('returns to the event')
            dt = ref_title(c.get('dialogue'))
            if dt:
                flags.append(f'opens dialogue {dt}')
            add(('   [' + '; '.join(flags) + ']') if flags else '', 'dim')
            add('\n')
            outs = c.get('outcomes') or []
            total = sum(max(0.0, _num(o.get('weight'), 0)) for o in outs) or 1
            for o in outs:
                pct = max(0.0, _num(o.get('weight'), 0)) / total
                add(f'   {pct:>4.0%}  ', 'pct')
                add(_s(o.get('label')) or 'outcome', 'b')
                bits = []
                res = o.get('resources') or []
                if res:
                    bits.append(', '.join(f'{RESOURCE.get(_int(r.get("kind")), "?")} {_num(r.get("amount")):+g}' for r in res))
                wt = writes_text(o.get('writes'))
                if wt:
                    bits.append(wt)
                if _int(o.get('detectionTurns')):
                    bits.append(f'hunt {_int(o.get("detectionTurns")):+d} turns')
                if _int(o.get('consumeAsteroid')):
                    bits.append('mines the asteroid')
                for key, lab in (('followUp', 'then'), ('scheduleEvent', 'schedules'), ('spawnOnMapEdge', 'spawns'), ('startCombatWith', 'fights')):
                    rt = ref_title(o.get(key))
                    if rt:
                        extra = f' in {_int(o.get("scheduleInTurns"), 1)} turn(s)' if key == 'scheduleEvent' else ''
                        bits.append(f'{lab} {rt}{extra}')
                for key, lab in (('recruitOfficer', 'recruits'), ('loseOfficer', 'loses')):
                    rt = ref_title(o.get(key))
                    if rt:
                        bits.append(f'{lab} {rt}')
                if _s(o.get('loseOfficerBestAt')):
                    bits.append(f'loses the best officer at {o.get("loseOfficerBestAt")}')
                if _s(o.get('returnCrewFromSiteCounter')):
                    bits.append(f'returns crew from site counter {o.get("returnCrewFromSiteCounter")}')
                if bits:
                    add('  — ' + '; '.join(bits), 'dim')
                add('\n')
                rt = _s(o.get('resultText')).strip()
                if rt:
                    add('         “' + rt.replace('\n', ' ') + '”\n', 'quote')
    elif n.kind == 'table':
        add('Rolled by: ', 'b'); add(n.subtitle + '\n\n')
        for e in g.edges:
            if e.src == nid:
                add(f'  {e.label:<14}', 'pct'); add(g.nodes[e.dst].title + '\n')
    elif n.kind == 'option':
        add('Kind: ', 'b'); add(POI_KIND.get(_int(d.get('kind')), '?') + '\n')
        ct = cond_text(d.get('conditions'))
        if ct:
            add('Shown when: ', 'b'); add(ct + '\n')
        add('Once per planet: ', 'b'); add(('yes' if _int(d.get('oncePerPlanet')) else 'no') + '\n')
        if _s(d.get('description')):
            add('\n' + _s(d.get('description')) + '\n', 'quote')
    elif n.kind == 'dialogue':
        if '__error__' in d:
            add('Invalid JSON: ' + d['__error__'] + '\n', 'err')
        else:
            chars = d.get('characters') or {}
            add(f'Left: {_s(chars.get("left"))}   Right: {_s(chars.get("right"))}\n\n', 'dim')
            choices = {cs.get('tick'): cs for cs in d.get('choices') or []}
            for l in sorted(d.get('lines') or [], key=lambda x: x.get('tick', 0)):
                add(f'{l.get("tick"):>3} ', 'pct'); add(f'{_s(l.get("speaker"))}: ', 'b'); add(_s(l.get('text')))
                if l.get('nextTick'):
                    add(f'  → {l.get("nextTick")}', 'dim')
                add('\n')
                cs = choices.pop(l.get('tick'), None)
                if cs:
                    for o in cs.get('options') or []:
                        add(f'      ◆ {_s(o.get("text"))}', 'h2'); add(f'  → {o.get("nextTick") or "next"}\n', 'dim')
    elif n.kind == 'enemy':
        caps = d.get('capabilities') or {}
        add(f'Hull {_s(d.get("maxHull"))} · Shields {_s(caps.get("maxShields"))} · Damage {_s(caps.get("weaponDamage"))}\n')
    elif n.kind == 'start':
        add('QuestScheduler start event: fires once from this turn, when the event\'s conditions hold.\n')
    elif n.kind == 'ending':
        add('Reached from:\n', 'b')
        for e in g.edges:
            if e.dst == nid:
                add(f'  {g.nodes[e.src].title}: {e.label}\n')

    # incoming / outgoing summary
    ins = [e for e in g.edges if e.dst == nid and e.kind != 'flag']
    if ins and n.kind != 'ending':
        add('\nReached from\n', 'h2')
        for e in ins:
            add(f'  {g.nodes[e.src].title}', 'link:' + e.src); add(f'  ({e.kind}: {e.label})\n', 'dim')
    fl_in = [e for e in g.edges if e.dst == nid and e.kind == 'flag']
    if fl_in:
        add('\nUnlocked by flags from\n', 'h2')
        for e in fl_in:
            add(f'  {g.nodes[e.src].title}', 'link:' + e.src); add(f'  ({e.label})\n', 'dim')
    mine = [i for i in g.issues if i.node_id == nid]
    if mine:
        add('\nChecks\n', 'h2')
        for i in mine:
            add(f'  {i.level.upper()}: {i.text}\n', 'err' if i.level == 'error' else 'warn' if i.level == 'warning' else 'dim')
    return out
