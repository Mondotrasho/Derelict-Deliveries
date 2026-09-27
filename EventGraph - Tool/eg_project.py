"""
Reads a Unity project for the Event Graph tool. Strictly read-only.

- Watches Assets/ for *.asset, *.json, *.meta and the chosen scene by
  (mtime, size). poll() reports what changed; nothing is re-read unless it did.
- Resolves GUIDs through .meta files.
- Parses only what the graph needs: EventDefinition / EventTable /
  EnemyShipDefinition assets, a handful of scene components, and dialogue
  JSON files that something references.
- A file that fails to parse (e.g. Unity is mid-save) keeps its last good
  parse and is reported, so the graph never blanks out.
"""
import json
import os
import re

import eg_yaml

TRACKED_EXT = ('.asset', '.json', '.meta', '.unity', '.psd', '.psb', '.png', '.jpg', '.jpeg', '.tga', '.gif')
ASSET_CLASSES = ('EventDefinition', 'EventTable', 'EnemyShipDefinition', 'BannerLibrary')
SCENE_CLASSES = ('PointOfInterestController', 'QuestScheduler', 'WarpExitController', 'PlanetManager')

GUID_LINE = re.compile(r'^guid: ([0-9a-fA-F]{32})', re.M)
SCRIPT_GUID = re.compile(r'm_Script: \{fileID: \d+, guid: ([0-9a-fA-F]{32})')
CLASS_ID = re.compile(r'm_EditorClassIdentifier: \S*?::([\w.]+)')
GO_NAME = re.compile(r'^  m_Name: (.*)$', re.M)
GO_REF = re.compile(r'^  m_GameObject: \{fileID: (-?\d+)\}', re.M)
TABLE_REF = re.compile(r'^  table: \{fileID: \d+, guid: ([0-9a-fA-F]{32})', re.M)


def guid_of(ref):
    """GUID string from a Unity object reference dict, or None."""
    if isinstance(ref, dict):
        g = ref.get('guid')
        if g is None:
            return None
        g = str(g)
        return g.zfill(32) if g.isdigit() else g
    return None


class AssetObj:
    def __init__(self, path, guid, cls, data):
        self.path = path
        self.guid = guid
        self.cls = cls
        self.data = data or {}

    @property
    def name(self):
        return os.path.splitext(os.path.basename(self.path))[0]


class Snapshot:
    """Everything the model needs, from one consistent read of the project."""
    def __init__(self):
        self.guid_to_path = {}
        self.script_class = {}        # script guid -> class name
        self.assets = {}              # guid -> AssetObj (interesting classes only)
        self.scene_path = None
        self.poi = None               # PointOfInterestController data
        self.scheduler = None         # QuestScheduler data
        self.warp = None              # WarpExitController data
        self.planets = []             # PlanetManager planets (id, displayName, tags)
        self.sources = []             # {'label', 'table', 'tags', 'enabled'}
        self.dialogues = {}           # guid -> parsed JSON dict or {'__error__': msg}
        self.parse_errors = []        # (path, message)


class Project:
    def __init__(self, root, scene=None):
        self.root = os.path.abspath(root)
        self.assets_dir = os.path.join(self.root, 'Assets')
        self.scene = scene            # path relative to root, or None = auto
        self._stamps = {}             # path -> (mtime, size)
        self._cache = {}              # path -> (stamp, value)
        self._last_good = {}          # path -> last successfully parsed value

    # ------------------------------------------------------------------ scan
    def list_scenes(self):
        out = []
        for dirpath, dirnames, filenames in os.walk(self.assets_dir):
            for f in filenames:
                if f.endswith('.unity'):
                    out.append(os.path.relpath(os.path.join(dirpath, f), self.root).replace('\\', '/'))
        return sorted(out)

    def _walk(self):
        stamps = {}
        for dirpath, dirnames, filenames in os.walk(self.assets_dir):
            for f in filenames:
                if f.endswith(TRACKED_EXT):
                    p = os.path.join(dirpath, f)
                    try:
                        st = os.stat(p)
                    except OSError:
                        continue
                    stamps[p] = (st.st_mtime_ns, st.st_size)
        return stamps

    def poll(self):
        """Return the set of paths added, removed or modified since last poll."""
        new = self._walk()
        changed = {p for p in new if self._stamps.get(p) != new[p]}
        changed |= {p for p in self._stamps if p not in new}
        self._stamps = new
        return changed

    # ------------------------------------------------------------------ read
    def _cached(self, path, reader):
        stamp = self._stamps.get(path)
        hit = self._cache.get(path)
        if hit and hit[0] == stamp:
            return hit[1]
        value = reader(path)
        self._cache[path] = (stamp, value)
        return value

    @staticmethod
    def _read_text(path):
        with open(path, encoding='utf-8', errors='replace') as f:
            return f.read()

    def load(self):
        if not self._stamps:
            self.poll()
        snap = Snapshot()

        # GUIDs
        for p in self._stamps:
            if not p.endswith('.meta'):
                continue
            guid = self._cached(p, self._read_meta_guid)
            if guid:
                target = p[:-5]
                snap.guid_to_path[guid] = target
                if target.endswith('.cs'):
                    snap.script_class[guid] = os.path.splitext(os.path.basename(target))[0]

        path_to_guid = {v: k for k, v in snap.guid_to_path.items()}

        # Assets
        for p in self._stamps:
            if not p.endswith('.asset'):
                continue
            try:
                result = self._cached(p, lambda path: self._read_asset(path, snap.script_class))
            except Exception as e:  # keep going; report
                snap.parse_errors.append((p, str(e)))
                continue
            if result is None:
                continue
            cls, data, err = result
            if err:
                snap.parse_errors.append((p, err))
                good = self._last_good.get(p)
                if good is not None:
                    data = good          # Unity mid-save: keep showing the last good version
            else:
                self._last_good[p] = data
            guid = path_to_guid.get(p)
            if guid and cls in ASSET_CLASSES:
                snap.assets[guid] = AssetObj(p, guid, cls, data)

        # Scene
        scene_rel = self.scene or self._default_scene()
        if scene_rel:
            scene_path = os.path.join(self.root, scene_rel)
            snap.scene_path = scene_path
            if os.path.exists(scene_path):
                try:
                    scene = self._cached(scene_path, lambda path: self._read_scene(path, snap.script_class))
                    err = scene[5]
                    good = self._last_good.get(scene_path)
                    if (err or scene[0] is None) and good is not None:
                        scene = good          # Unity mid-save: keep the last complete read
                    elif not err:
                        self._last_good[scene_path] = scene
                    snap.poi, snap.scheduler, snap.warp, snap.planets, snap.sources, _ = scene
                    if err:
                        snap.parse_errors.append((scene_path, err))
                except Exception as e:
                    snap.parse_errors.append((scene_path, str(e)))
        return snap

    def load_dialogue(self, snap, guid):
        path = snap.guid_to_path.get(guid)
        if not path or not os.path.exists(path):
            return None
        return self._cached(path, self._read_json)

    def _default_scene(self):
        scenes = self.list_scenes()
        return scenes[0] if scenes else None

    # ------------------------------------------------------------ readers
    def _read_meta_guid(self, path):
        m = GUID_LINE.search(self._read_text(path))
        return m.group(1) if m else None

    def _read_asset(self, path, script_class):
        text = self._read_text(path)
        m = SCRIPT_GUID.search(text)
        cls = script_class.get(m.group(1)) if m else None
        if cls is None:
            c = CLASS_ID.search(text)
            cls = c.group(1).split('.')[-1] if c else None
        if cls not in ASSET_CLASSES:
            return None
        for _, _, body in eg_yaml.iter_documents(text):
            try:
                data = eg_yaml.parse(body)
            except Exception as e:
                return cls, {}, f'could not parse: {e}'
            if isinstance(data, dict) and 'MonoBehaviour' in data:
                return cls, data['MonoBehaviour'] or {}, None
        return cls, {}, 'no MonoBehaviour document'

    def _read_scene(self, path, script_class):
        text = self._read_text(path)
        go_names = {}
        wanted = []
        for class_id, file_id, body in eg_yaml.iter_documents(text):
            if class_id == 1:
                m = GO_NAME.search(body)
                if m:
                    go_names[file_id] = m.group(1).strip()
            elif class_id == 114:
                m = SCRIPT_GUID.search(body)
                cls = script_class.get(m.group(1)) if m else None
                if cls is None:
                    c = CLASS_ID.search(body)
                    cls = c.group(1).split('.')[-1] if c else None
                if cls in SCENE_CLASSES or TABLE_REF.search(body):
                    wanted.append((cls, body))

        poi = scheduler = warp = None
        planets, sources, errors = [], [], []
        for cls, body in wanted:
            try:
                data = (eg_yaml.parse(body) or {}).get('MonoBehaviour') or {}
            except Exception as e:
                errors.append(f'{cls}: {e}')
                continue
            go = GO_REF.search(body)
            go_name = go_names.get(go.group(1), '?') if go else '?'
            if cls == 'PointOfInterestController':
                poi = data
            elif cls == 'QuestScheduler':
                scheduler = data
            elif cls == 'WarpExitController':
                warp = data
            elif cls == 'PlanetManager':
                for p in data.get('planets') or []:
                    tags = ((p.get('eventState') or {}).get('tags')) or []
                    planets.append({'id': p.get('id'), 'displayName': p.get('displayName') or '', 'tags': [str(t) for t in tags]})
            table = guid_of(data.get('table'))
            if table:
                tags = [str(t) for t in (data.get('eventTags') or [])]
                enabled = data.get('sourceEnabled', 1) not in (0, False)
                sources.append({'label': f'{cls or "?"} on {go_name}', 'table': table, 'tags': tags, 'enabled': enabled})
        return poi, scheduler, warp, planets, sources, '; '.join(errors) if errors else None

    def _read_json(self, path):
        try:
            with open(path, encoding='utf-8-sig') as f:
                return json.load(f)
        except Exception as e:
            return {'__error__': str(e)}
