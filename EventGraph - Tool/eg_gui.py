"""
Tkinter window for the Event Graph tool. Read-only viewer of a Unity project's
event data, redrawn live whenever the files change on disk.
"""
import json
import os
import subprocess
import sys
import time
import tkinter as tk
import tkinter.font as tkfont
from tkinter import ttk, filedialog, messagebox

import eg_images
import eg_layout
import eg_model
import eg_project

SETTINGS_PATH = os.path.join(os.path.expanduser('~'), '.event_graph_tool.json')
POLL_MS = 1000
FLASH_SECONDS = 2.5

BG = '#171b24'
GRID = '#1f2430'
TEXT = '#f7fafc'
SUB = '#cbd5e0'
NODE_COLOURS = {
    ('event', 'Asteroid'): '#8a5a12', ('event', 'Planet'): '#1f5f9e', ('event', 'Derelict'): '#46505f',
    ('event', 'Hazard'): '#9b2c2c', ('event', 'Pickup'): '#8a7408', ('event', '?'): '#555',
    'table': '#2d3748', 'option': '#1d6f6f', 'start': '#226b3e', 'dialogue': '#5b3aa6',
    'enemy': '#7f1d1d', 'ending': '#b7791f',
}
EDGE_STYLE = {   # colour, dash, width
    'entry': ('#8f9bb0', None, 1.2), 'follow': ('#e2e8f0', None, 1.6), 'schedule': ('#f6ad55', (6, 4), 1.6),
    'dialogue': ('#b794f4', None, 1.2), 'spawn': ('#fc8181', None, 2.4), 'combat': ('#fc8181', None, 2.4),
    'ending': ('#f6e05e', None, 1.4), 'banner': ('#63b3ed', (2, 3), 1.4), 'story': ('#68d391', (6, 3), 1.6), 'flag': ('#5a6478', (2, 4), 1.0),
}
EDGE_GROUPS = [('entry', 'Entries (table / option / start)'), ('follow', 'Follow-ups'), ('schedule', 'Scheduled (N turns)'),
               ('dialogue', 'Dialogues'), ('spawn', 'Spawn / combat'), ('ending', 'Endings'), ('banner', 'Banner swaps (Show Banner Of)'),
               ('story', 'Story links (quest flags)'), ('flag', 'Other flag links')]
NODE_GROUPS = [('Asteroid', 'Asteroid events'), ('Planet', 'Planet events'), ('Derelict', 'Derelict events'),
               ('Hazard', 'Hazard events'), ('Pickup', 'Pickups'), ('table', 'Tables'), ('option', 'Planet options'),
               ('start', 'Quest starts'), ('dialogue', 'Dialogues'), ('enemy', 'Enemies'), ('ending', 'Endings')]


def load_settings():
    try:
        with open(SETTINGS_PATH, encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return {}


def save_settings(data):
    try:
        with open(SETTINGS_PATH, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=1)
    except Exception:
        pass


class App(tk.Tk):
    def __init__(self, project_root=None, scene=None):
        super().__init__()
        self.title('Event Graph v2 · read-only')
        self.settings = load_settings()
        self.geometry(self.settings.get('geometry', '1500x900'))
        self.configure(bg=BG)

        self.project = None
        self.snap = None
        self.g = None
        self.pos, self.sizes = {}, {}
        self.visible, self.vis_edges = set(), []
        self.zoom, self.offx, self.offy = 1.0, -40.0, -40.0
        self.selected = None
        self.highlight = set()
        self.multi = set()              # multi-selection (rubber band, Ctrl/Shift+click, group drag)
        self._image_cache = {}          # (path, mtime, width) -> PhotoImage
        self._shown_images = []         # keep references alive while shown
        self.flash = {}
        self.pinned = {}
        self._fonts = {}
        self._redraw_pending = False
        self._drag = None
        self._search_hits, self._search_i = [], -1
        self._last_update = None
        self._first_fit = True

        self._build_ui()
        root = project_root or self.settings.get('project')
        if root and os.path.isdir(os.path.join(root, 'Assets')):
            self.open_project(root, scene or (self.settings.get('scene') if root == self.settings.get('project') else None))
        else:
            self._status('Open a Unity project folder (the one that contains Assets/).', 'warn')
        self.after(POLL_MS, self._poll)
        self.protocol('WM_DELETE_WINDOW', self._on_close)

    # ================================================================== UI
    def _build_ui(self):
        style = ttk.Style(self)
        try:
            style.theme_use('clam')
        except tk.TclError:
            pass
        style.configure('Treeview', rowheight=22)

        bar = ttk.Frame(self, padding=(6, 4))
        bar.pack(side='top', fill='x')
        ttk.Button(bar, text='Open project…', command=self._choose_project).pack(side='left')
        ttk.Label(bar, text='  Scene:').pack(side='left')
        self.scene_var = tk.StringVar()
        self.scene_box = ttk.Combobox(bar, textvariable=self.scene_var, state='readonly', width=42)
        self.scene_box.pack(side='left')
        self.scene_box.bind('<<ComboboxSelected>>', lambda e: self._change_scene())
        ttk.Button(bar, text='Fit (F)', command=self.fit).pack(side='left', padx=(10, 0))
        ttk.Button(bar, text='Re-layout', command=self._relayout).pack(side='left', padx=(4, 0))
        ttk.Button(bar, text='Export Mermaid…', command=self._export_mermaid).pack(side='left', padx=(4, 0))
        ttk.Label(bar, text='   Find:').pack(side='left')
        self.search_var = tk.StringVar()
        search = ttk.Entry(bar, textvariable=self.search_var, width=24)
        search.pack(side='left')
        search.bind('<Return>', lambda e: self._find_next())
        self.search_var.trace_add('write', lambda *a: self._search_changed())
        self.live_label = tk.Label(bar, text='', anchor='e')
        self.live_label.pack(side='right')

        panes = ttk.PanedWindow(self, orient='horizontal')
        panes.pack(fill='both', expand=True)

        # -- filters
        left = ttk.Frame(panes, padding=6)
        panes.add(left, weight=0)
        self.node_vars, self.edge_vars = {}, {}
        saved = self.settings.get('filters', {})
        ttk.Label(left, text='Show', font=('TkDefaultFont', 10, 'bold')).pack(anchor='w')
        for key, label in NODE_GROUPS:
            v = tk.BooleanVar(value=saved.get('n:' + key, True))
            self.node_vars[key] = v
            ttk.Checkbutton(left, text=label, variable=v, command=self._filters_changed).pack(anchor='w')
        ttk.Separator(left).pack(fill='x', pady=6)
        ttk.Label(left, text='Links', font=('TkDefaultFont', 10, 'bold')).pack(anchor='w')
        for key, label in EDGE_GROUPS:
            v = tk.BooleanVar(value=saved.get('e:' + key, key != 'flag'))
            self.edge_vars[key] = v
            ttk.Checkbutton(left, text=label, variable=v, command=self._filters_changed).pack(anchor='w')
        ttk.Label(left, text='Flag links starting with:').pack(anchor='w', pady=(6, 0))
        self.prefix_var = tk.StringVar(value=saved.get('prefix', ''))
        pe = ttk.Entry(left, textvariable=self.prefix_var, width=22)
        pe.pack(anchor='w')
        self.prefix_var.trace_add('write', lambda *a: self._filters_changed())
        ttk.Separator(left).pack(fill='x', pady=6)
        ttk.Label(left, text='Focus on selection', font=('TkDefaultFont', 10, 'bold')).pack(anchor='w')
        self.focus_var = tk.StringVar(value='all')
        for val, label in (('all', 'Everything'), ('flow', 'Its whole flow (group)'), ('down', 'What it leads to'),
                           ('up', 'What leads to it'), ('both', 'Leads to + leads from')):
            ttk.Radiobutton(left, text=label, value=val, variable=self.focus_var, command=self._filters_changed).pack(anchor='w')
        self.focus_flags_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(left, text='…also through other flag links', variable=self.focus_flags_var, command=self._filters_changed).pack(anchor='w')
        ttk.Separator(left).pack(fill='x', pady=6)
        ttk.Label(left, text='Link labels', font=('TkDefaultFont', 10, 'bold')).pack(anchor='w')
        self.labels_var = tk.StringVar(value=saved.get('labels', 'selected'))
        for val, label in (('selected', 'Selected node only'), ('all', 'All'), ('none', 'None')):
            ttk.Radiobutton(left, text=label, value=val, variable=self.labels_var, command=self.request_redraw).pack(anchor='w')
        self.groups_var = tk.BooleanVar(value=saved.get('groups', True))
        ttk.Checkbutton(left, text='Group into flows (boxes)', variable=self.groups_var, command=self._filters_changed).pack(anchor='w', pady=(6, 0))
        self.hide_lonely_var = tk.BooleanVar(value=saved.get('hideLonely', False))
        ttk.Checkbutton(left, text='Hide unconnected nodes', variable=self.hide_lonely_var, command=self._filters_changed).pack(anchor='w', pady=(6, 0))
        ttk.Separator(left).pack(fill='x', pady=6)
        legend = tk.Canvas(left, width=200, height=98, bg=BG, highlightthickness=0)
        legend.pack(anchor='w')
        short = {'entry': 'Entry', 'follow': 'Follow-up', 'schedule': 'Scheduled', 'dialogue': 'Dialogue',
                 'spawn': 'Spawn/fight', 'ending': 'Ending', 'banner': 'Banner swap', 'story': 'Story link', 'flag': 'Other flag'}
        for i, (key, _) in enumerate(EDGE_GROUPS):
            col, dash, w = EDGE_STYLE[key]
            x0, y0 = 6 + (i % 2) * 100, 10 + (i // 2) * 16
            legend.create_line(x0, y0, x0 + 26, y0, fill=col, dash=dash, width=w + 0.5, arrow='last', arrowshape=(6, 7, 3))
            legend.create_text(x0 + 31, y0, text=short[key], fill=SUB, anchor='w', font=('TkDefaultFont', 8))

        # -- canvas
        mid = ttk.Frame(panes)
        panes.add(mid, weight=1)
        self.canvas = tk.Canvas(mid, bg=BG, highlightthickness=0)
        self.canvas.pack(fill='both', expand=True)
        c = self.canvas
        c.bind('<ButtonPress-1>', self._on_press)
        c.bind('<B1-Motion>', self._on_drag)
        c.bind('<ButtonRelease-1>', self._on_release)
        c.bind('<Double-Button-1>', self._on_double)
        c.bind('<ButtonPress-3>', self._on_context)
        c.bind('<ButtonPress-2>', self._on_middle_press)
        c.bind('<B2-Motion>', self._on_middle_drag)
        c.bind('<MouseWheel>', self._on_wheel)
        c.bind('<Button-4>', lambda e: self._zoom_at(e.x, e.y, 1.15))
        c.bind('<Button-5>', lambda e: self._zoom_at(e.x, e.y, 1 / 1.15))
        c.bind('<Motion>', self._on_motion)
        c.bind('<Leave>', lambda e: c.delete('tip'))
        c.bind('<Configure>', lambda e: self.request_redraw())
        self.bind('<KeyPress-f>', lambda e: self.fit() if self.focus_get() is not None and not isinstance(self.focus_get(), (tk.Entry, ttk.Entry)) else None)
        self.bind('<Escape>', lambda e: self._clear_selection())

        # -- side panel
        right = ttk.Notebook(panes)
        panes.add(right, weight=0)
        self.notebook = right
        dframe = ttk.Frame(right)
        self.details = tk.Text(dframe, wrap='word', width=58, bg='#10141c', fg=TEXT, insertbackground=TEXT,
                               relief='flat', padx=10, pady=8, font=('TkDefaultFont', 10))
        sb = ttk.Scrollbar(dframe, command=self.details.yview)
        self.details.configure(yscrollcommand=sb.set, state='disabled')
        sb.pack(side='right', fill='y')
        self.details.pack(fill='both', expand=True)
        t = self.details
        t.tag_configure('h1', font=('TkDefaultFont', 14, 'bold'))
        t.tag_configure('h2', font=('TkDefaultFont', 10, 'bold'), foreground='#90cdf4')
        t.tag_configure('b', font=('TkDefaultFont', 10, 'bold'))
        t.tag_configure('dim', foreground='#a0aec0')
        t.tag_configure('path', foreground='#718096', font=('TkFixedFont', 9))
        t.tag_configure('quote', foreground='#e2e8f0', font=('TkDefaultFont', 10, 'italic'), lmargin1=10, lmargin2=10)
        t.tag_configure('pct', foreground='#f6ad55', font=('TkFixedFont', 10))
        t.tag_configure('err', foreground='#fc8181')
        t.tag_configure('warn', foreground='#f6ad55')
        right.add(dframe, text='Details')

        fframe = ttk.Frame(right)
        self.flag_tree = ttk.Treeview(fframe, columns=('scope', 'w', 'r'), selectmode='browse')
        self.flag_tree.heading('#0', text='Flag / tag / counter')
        self.flag_tree.heading('scope', text='Scope')
        self.flag_tree.heading('w', text='Set by')
        self.flag_tree.heading('r', text='Read by')
        self.flag_tree.column('#0', width=250)
        for col in ('scope', 'w', 'r'):
            self.flag_tree.column(col, width=60, anchor='center')
        fsb = ttk.Scrollbar(fframe, command=self.flag_tree.yview)
        self.flag_tree.configure(yscrollcommand=fsb.set)
        fsb.pack(side='right', fill='y')
        self.flag_tree.pack(fill='both', expand=True)
        self.flag_tree.bind('<<TreeviewSelect>>', lambda e: self._flag_selected())
        right.add(fframe, text='Flags')

        cframe = ttk.Frame(right)
        self.check_tree = ttk.Treeview(cframe, columns=('level',), selectmode='browse')
        self.check_tree.heading('#0', text='Check')
        self.check_tree.heading('level', text='Level')
        self.check_tree.column('#0', width=420)
        self.check_tree.column('level', width=70, anchor='center')
        self.check_tree.tag_configure('error', foreground='#c53030')
        self.check_tree.tag_configure('warning', foreground='#b7791f')
        self.check_tree.tag_configure('info', foreground='#718096')
        csb = ttk.Scrollbar(cframe, command=self.check_tree.yview)
        self.check_tree.configure(yscrollcommand=csb.set)
        csb.pack(side='right', fill='y')
        self.check_tree.pack(fill='both', expand=True)
        self.check_tree.bind('<<TreeviewSelect>>', lambda e: self._check_selected())
        right.add(cframe, text='Checks')

        bframe = ttk.Frame(right)
        self.banner_text = tk.Text(bframe, wrap='word', width=58, bg='#10141c', fg=TEXT, relief='flat',
                                   padx=10, pady=8, font=('TkDefaultFont', 10))
        bsb = ttk.Scrollbar(bframe, command=self.banner_text.yview)
        self.banner_text.configure(yscrollcommand=bsb.set, state='disabled')
        bsb.pack(side='right', fill='y')
        self.banner_text.pack(fill='both', expand=True)
        for tname, cfg in (('h1', dict(font=('TkDefaultFont', 14, 'bold'))), ('h2', dict(font=('TkDefaultFont', 10, 'bold'), foreground='#90cdf4')),
                           ('b', dict(font=('TkDefaultFont', 10, 'bold'))), ('dim', dict(foreground='#a0aec0')),
                           ('warn', dict(foreground='#f6ad55')), ('err', dict(foreground='#fc8181'))):
            self.banner_text.tag_configure(tname, **cfg)
        right.add(bframe, text='Banners')
        self._banner_images = []

        self.status = ttk.Label(self, anchor='w', padding=(6, 2))
        self.status.pack(side='bottom', fill='x')

        self.menu = tk.Menu(self, tearoff=0)

    # ============================================================ project
    def _choose_project(self):
        d = filedialog.askdirectory(title='Unity project folder (contains Assets/)', initialdir=self.settings.get('project') or os.getcwd())
        if d:
            if not os.path.isdir(os.path.join(d, 'Assets')):
                messagebox.showerror('Event Graph', 'That folder has no Assets/ folder. Pick the Unity project folder.')
                return
            self.open_project(d)

    def open_project(self, root, scene=None):
        self.project = eg_project.Project(root)
        scenes = self.project.list_scenes()
        self.scene_box['values'] = scenes
        if scene not in scenes:
            preferred = [s for s in scenes if 'Oscars_Test_Movement' in s]
            scene = (preferred or scenes or [None])[0]
        self.project.scene = scene
        self.scene_var.set(scene or '')
        self.pinned = dict(self.settings.get('pinned', {}).get(root, {}))
        self.settings['project'] = root
        self.settings['scene'] = scene
        self.title(f'Event Graph · {os.path.basename(root)} · read-only')
        self._first_fit = True
        self.project.poll()
        self.reload(None)

    def _change_scene(self):
        if not self.project:
            return
        self.project.scene = self.scene_var.get() or None
        self.settings['scene'] = self.project.scene
        self.reload(None)

    def _poll(self):
        try:
            if self.project:
                changed = self.project.poll()
                if changed:
                    self.reload(changed)
            now = time.time()
            if self.flash and any(t < now for t in self.flash.values()):
                self.flash = {k: t for k, t in self.flash.items() if t >= now}
                self.request_redraw()
        except Exception as e:  # never let the poll loop die
            self._status(f'Reload failed: {e}', 'error')
        finally:
            self.after(POLL_MS, self._poll)

    def reload(self, changed):
        snap = self.project.load()
        g = eg_model.build(snap, self.project)
        self.snap, self.g = snap, g
        if self.selected not in g.nodes:
            self.selected = None
        if changed:
            until = time.time() + FLASH_SECONDS
            for p in changed:
                base = p[:-5] if p.endswith('.meta') else p
                for nid in g.path_nodes.get(os.path.normcase(base), ()):
                    self.flash[nid] = until
        self._last_update = time.strftime('%H:%M:%S')
        self._refresh(relayout=True)
        self._fill_side_panels()
        errors = sum(1 for i in g.issues if i.level == 'error')
        warns = sum(1 for i in g.issues if i.level == 'warning')
        what = f'{len(changed)} file(s) changed' if changed else 'loaded'
        colour = 'error' if snap.parse_errors else 'ok'
        self._status_live(f'● live · {what} · {self._last_update}', colour)
        self._status(f'{len(g.nodes)} nodes, {len(g.edges)} links · {errors} errors, {warns} warnings · '
                     f'{self.project.root}  (Unity writes asset edits to disk on Save / Ctrl+S)')
        self.notebook.tab(2, text=f'Checks ({errors}E {warns}W)')
        if self._first_fit:
            self._first_fit = False
            self.after(50, self.fit)

    # ============================================================= visible set
    def _node_allowed(self, n):
        key = n.category if n.kind == 'event' else n.kind
        v = self.node_vars.get(key if key in self.node_vars else n.kind)
        return v.get() if v is not None else True

    def _compute_visible(self):
        g = self.g
        nodes = {nid for nid, n in g.nodes.items() if self._node_allowed(n)}
        focus = self.focus_var.get()
        if focus == 'flow' and self.selected in g.nodes:
            gid = g.nodes[self.selected].group
            keep = set(g.groups.get(gid, {}).get('nodes', [self.selected]))
            # plus whatever feeds the flow from outside (a table, a planet option, a start)
            keep |= {e.src for e in g.edges if e.dst in keep and e.kind == 'entry'}
            nodes = (nodes & keep) | {self.selected}
        elif focus != 'all' and self.selected in g.nodes:
            kinds = eg_model.PRIMARY + (('flag',) if self.focus_flags_var.get() else ())
            keep = {self.selected}
            prefix_f = self.prefix_var.get().strip()
            if focus in ('down', 'both'):
                keep |= eg_model.reachable(g, self.selected, True, kinds, prefix_f)
            if focus in ('up', 'both'):
                keep |= eg_model.reachable(g, self.selected, False, kinds, prefix_f)
            nodes = (nodes & keep) | {self.selected}
        prefix = self.prefix_var.get().strip()
        edges = []
        for e in g.edges:
            if not self.edge_vars.get('spawn' if e.kind == 'combat' else e.kind, tk.BooleanVar(value=True)).get():
                continue
            if e.src not in nodes or e.dst not in nodes:
                continue
            if e.kind == 'flag' and prefix and not e.label.startswith(prefix):
                continue
            edges.append(e)
        if self.hide_lonely_var.get():
            linked = {e.src for e in edges} | {e.dst for e in edges}
            nodes = {n for n in nodes if n in linked or n == self.selected}
        self.visible, self.vis_edges = nodes, edges

    def _measure(self):
        f_title = self._font(10, True)
        f_sub = self._font(8, False)
        for nid in self.visible:
            n = self.g.nodes[nid]
            w = max(f_title.measure(n.title), f_sub.measure(n.subtitle[:60])) + 26
            self.sizes[nid] = (min(300, max(120, w)), 44)

    def _refresh(self, relayout=False):
        if not self.g:
            return
        self._compute_visible()
        self._measure()
        if relayout:
            # Story links shape the layout even when hidden, so a quest reads in story order
            # (cult:egg is set by Journey's End, so what needs it sits to its right).
            links = [(e.src, e.dst) for e in self.g.edges
                     if e.kind not in ('flag', 'banner') and e.src in self.visible and e.dst in self.visible]
            nodes = {nid: self.g.nodes[nid] for nid in self.visible}
            cw, ch = max(400, self.canvas.winfo_width()), max(300, self.canvas.winfo_height())
            groups = {nid: self.g.nodes[nid].group for nid in self.visible} if self.groups_var.get() else None
            label_font = self._font(11, True)
            min_w = {gid: label_font.measure(f'{info["name"]}  ·  {len(info["nodes"])}') + 40
                     for gid, info in self.g.groups.items()} if groups else None
            self.pos = eg_layout.layout(nodes, links, self.sizes, self.pinned, aspect=cw / ch,
                                        groups=groups, group_min_width=min_w)
        self.request_redraw()

    def _filters_changed(self):
        self._save_filters()
        self._refresh(relayout=True)

    def _relayout(self):
        if self.project:
            self.pinned = {}
            self.settings.setdefault('pinned', {})[self.project.root] = {}
            save_settings(self.settings)
        self._refresh(relayout=True)
        self.fit()

    # ================================================================ drawing
    def _font(self, size, bold):
        key = (int(size), bold)
        if key not in self._fonts:
            self._fonts[key] = tkfont.Font(family=tkfont.nametofont('TkDefaultFont').actual('family'),
                                          size=max(1, int(size)), weight='bold' if bold else 'normal')
        return self._fonts[key]

    def request_redraw(self):
        if not self._redraw_pending:
            self._redraw_pending = True
            self.after_idle(self._redraw)

    def _sx(self, x):
        return (x - self.offx) * self.zoom

    def _sy(self, y):
        return (y - self.offy) * self.zoom

    def _redraw(self):
        self._redraw_pending = False
        c = self.canvas
        c.delete('all')
        if not self.g:
            return
        z = self.zoom
        if self.groups_var.get():
            self._draw_groups()
        labels = self.labels_var.get()
        sel_edges = {id(e) for e in self.vis_edges if self.selected in (e.src, e.dst)}
        for idx, e in enumerate(self.vis_edges):
            if e.src not in self.pos or e.dst not in self.pos:
                continue
            show_label = labels == 'all' or (labels == 'selected' and id(e) in sel_edges)
            self._draw_edge(idx, e, show_label, id(e) in sel_edges)
        issues = {}
        for i in self.g.issues:
            if i.node_id and i.level != 'info':
                issues[i.node_id] = 'error' if i.level == 'error' or issues.get(i.node_id) == 'error' else 'warning'
        now = time.time()
        for nid in self.visible:
            if nid not in self.pos:
                continue
            self._draw_node(nid, issues.get(nid), self.flash.get(nid, 0) > now)

    def _draw_groups(self):
        c, z = self.canvas, self.zoom
        members = {}
        for nid in self.visible:
            if nid in self.pos:
                members.setdefault(self.g.nodes[nid].group, []).append(nid)
        sel_group = self.g.nodes[self.selected].group if self.selected in self.g.nodes else None
        for gid, ids in members.items():
            info = self.g.groups.get(gid)
            if not info or (len(ids) < 2 and len(info['nodes']) < 2):
                continue
            x1 = min(self.pos[n][0] for n in ids) - 16
            y1 = min(self.pos[n][1] for n in ids) - 34
            x2 = max(self.pos[n][0] + self.sizes.get(n, (160, 44))[0] for n in ids) + 16
            y2 = max(self.pos[n][1] + self.sizes.get(n, (160, 44))[1] for n in ids) + 16
            outline = '#8fb3ff' if gid == sel_group else '#34405a'
            self._round_rect(self._sx(x1), self._sy(y1), self._sx(x2), self._sy(y2), 12 * z,
                             fill='#1d2331', outline=outline, width=2 if gid == sel_group else 1, tags=('group', 'g:' + gid))
            if z >= 0.25:
                font = self._font(11 * max(z, 0.6), True)
                text = self._fit_text(f'{info["name"]}  ·  {len(info["nodes"])}', font, (x2 - x1) * z - 18 * z)
                c.create_text(self._sx(x1) + 12 * z, self._sy(y1) + 16 * z, text=text,
                              anchor='w', fill='#8fb3ff' if gid == sel_group else '#7f8ea8',
                              font=font, tags=('group', 'g:' + gid))

    def _box(self, nid):
        x, y = self.pos[nid]
        w, h = self.sizes.get(nid, (160, 44))
        return self._sx(x), self._sy(y), self._sx(x + w), self._sy(y + h)

    def _draw_node(self, nid, issue, flashing):
        c, z = self.canvas, self.zoom
        n = self.g.nodes[nid]
        x1, y1, x2, y2 = self._box(nid)
        if x2 < 0 or y2 < 0 or x1 > c.winfo_width() or y1 > c.winfo_height():
            return
        fill = NODE_COLOURS.get((n.kind, n.category)) or NODE_COLOURS.get(n.kind, '#444')
        outline, width = '#0b0e14', 1
        if nid in self.highlight:
            outline, width = '#68d391', 3
        if nid in self.multi:
            outline, width = '#63b3ed', 3
        if nid == self.selected:
            outline, width = '#ffffff', 3
        if flashing:
            outline, width = '#fbd38d', 4
        tag = ('node', 'n:' + nid)
        if n.kind in ('option', 'ending', 'start'):
            r = min(14 * z, (y2 - y1) / 2)
            self._round_rect(x1, y1, x2, y2, r, fill=fill, outline=outline, width=width, tags=tag)
        else:
            c.create_rectangle(x1, y1, x2, y2, fill=fill, outline=outline, width=width, tags=tag)
        if n.kind == 'event':
            c.create_rectangle(x1, y1, x1 + max(2, 5 * z), y2, fill='#ffffff', outline='', stipple='gray50', tags=tag)
        if z >= 0.3:
            tf, sf = self._font(10 * z, True), self._font(8 * z, False)
            maxw = x2 - x1 - 16 * z
            title = self._fit_text(n.title, tf, maxw)
            if z >= 0.55:
                c.create_text(x1 + 10 * z, y1 + 14 * z, text=title, fill=TEXT if n.kind != 'ending' else '#1a1a1a', font=tf, anchor='w', tags=tag)
                c.create_text(x1 + 10 * z, y1 + 31 * z, text=self._fit_text(n.subtitle, sf, maxw), fill=SUB if n.kind != 'ending' else '#3a2a00', font=sf, anchor='w', tags=tag)
            else:
                c.create_text((x1 + x2) / 2, (y1 + y2) / 2, text=title, fill=TEXT, font=tf, tags=tag)
        if issue:
            r = max(3, 5 * z)
            c.create_oval(x2 - 2 * r - 3, y1 + 3, x2 - 3, y1 + 3 + 2 * r, fill='#fc8181' if issue == 'error' else '#f6ad55', outline='', tags=tag)

    def _round_rect(self, x1, y1, x2, y2, r, **kw):
        pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2, x2 - r, y2,
               x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
        return self.canvas.create_polygon(pts, smooth=True, **kw)

    @staticmethod
    def _fit_text(s, font, maxw):
        if font.measure(s) <= maxw:
            return s
        while s and font.measure(s + '…') > maxw:
            s = s[:-1]
        return s + '…'

    def _draw_edge(self, idx, e, show_label, is_selected):
        c, z = self.canvas, self.zoom
        col, dash, w = EDGE_STYLE.get(e.kind, ('#aaa', None, 1))
        sx1, sy1, sx2, sy2 = self._box(e.src)
        dx1, dy1, dx2, dy2 = self._box(e.dst)
        width = max(1, w * z * (1.8 if is_selected else 1))
        tags = ('edge', f'e:{idx}')
        if dx1 > sx2 + 4:        # forward: right side -> left side
            x1, y1 = sx2, (sy1 + sy2) / 2
            x2, y2 = dx1, (dy1 + dy2) / 2
            mx = (x1 + x2) / 2
            pts = [x1, y1, mx, y1, mx, y2, x2, y2]
            lx, ly = mx, (y1 + y2) / 2
        else:                    # backwards or same column: loop underneath
            x1, y1 = (sx1 + sx2) / 2, sy2
            x2, y2 = (dx1 + dx2) / 2, dy2
            drop = max(y1, y2) + 40 * z
            pts = [x1, y1, x1, drop, x2, drop, x2, y2]
            lx, ly = (x1 + x2) / 2, drop
        c.create_line(*pts, smooth=True, splinesteps=24, fill=col, width=width, dash=dash,
                      arrow='last', arrowshape=(10 * z + 2, 12 * z + 2, 4 * z + 1), tags=tags)
        if show_label and e.label and z >= 0.45:
            f = self._font(8 * max(z, 0.8), False)
            t = c.create_text(lx, ly, text=e.label if len(e.label) < 48 else e.label[:46] + '…', fill=col, font=f, tags=tags)
            bx = c.bbox(t)
            if bx:
                r = c.create_rectangle(bx[0] - 3, bx[1] - 1, bx[2] + 3, bx[3] + 1, fill=BG, outline='', tags=tags)
                c.tag_lower(r, t)

    # =========================================================== interaction
    def _item_under(self, x, y):
        """What is under the cursor: ('node', id), ('edge', index), ('group', gid) or (None, None)."""
        for item in reversed(self.canvas.find_overlapping(x - 2, y - 2, x + 2, y + 2)):
            for t in self.canvas.gettags(item):
                if t.startswith('n:'):
                    return 'node', t[2:]
        for item in reversed(self.canvas.find_overlapping(x - 3, y - 3, x + 3, y + 3)):
            for t in self.canvas.gettags(item):
                if t.startswith('e:'):
                    return 'edge', int(t[2:])
        for item in reversed(self.canvas.find_overlapping(x, y, x, y)):
            for t in self.canvas.gettags(item):
                if t.startswith('g:'):
                    return 'group', t[2:]
        return None, None

    # Mouse, left button:
    #   node                 select it and drag it (or drag the whole multi-selection it belongs to)
    #   Ctrl/Shift + node    add it to / remove it from the multi-selection
    #   group box / label    drag the whole flow
    #   Shift/Ctrl + drag    rubber-band select (also over a flow's box)
    #   empty space          pan (a click without moving clears the selection)
    SHIFT, CTRL = 0x0001, 0x0004

    def _on_press(self, ev):
        self.canvas.focus_set()
        kind, obj = self._item_under(ev.x, ev.y)
        additive = bool(ev.state & (self.SHIFT | self.CTRL))
        start = {'x': ev.x, 'y': ev.y, 'moved': False}
        if kind == 'node':
            if additive:
                self.multi ^= {obj}
                if len(self.multi) == 1:
                    self.select(next(iter(self.multi)))
                self._show_multi_details()
                self.request_redraw()
                self._drag = dict(start, mode='none')
                return
            if obj not in self.multi:
                self.multi = set()
                self.select(obj)
            else:
                self.selected = obj
            movers = self.multi if obj in self.multi and len(self.multi) > 1 else {obj}
            self._drag = dict(start, mode='nodes', orig={n: self.pos[n] for n in movers if n in self.pos})
        elif additive:
            self._drag = dict(start, mode='band')     # Shift/Ctrl+drag: rubber band, even over a flow's box
        elif kind == 'group':
            members = [n for n in self.visible if n in self.pos and self.g.nodes[n].group == obj]
            self.multi = set(members)
            self._show_multi_details(self.g.groups.get(obj, {}).get('name'))
            self._drag = dict(start, mode='nodes', orig={n: self.pos[n] for n in members})
            self.request_redraw()
        else:
            self._drag = dict(start, mode='pan', ox=self.offx, oy=self.offy)

    def _on_drag(self, ev):
        d = self._drag
        if not d or d['mode'] == 'none':
            return
        dx, dy = (ev.x - d['x']) / self.zoom, (ev.y - d['y']) / self.zoom
        if abs(ev.x - d['x']) + abs(ev.y - d['y']) >= 3:
            d['moved'] = True
        if d['mode'] == 'nodes':
            for n, (ox, oy) in d['orig'].items():
                self.pos[n] = (ox + dx, oy + dy)
            self.request_redraw()
        elif d['mode'] == 'pan':
            self.offx, self.offy = d['ox'] - dx, d['oy'] - dy
            self.request_redraw()
        elif d['mode'] == 'band':
            self.canvas.delete('band')
            self.canvas.create_rectangle(d['x'], d['y'], ev.x, ev.y, outline='#63b3ed', dash=(4, 3),
                                         fill='#63b3ed', stipple='gray12', tags='band')

    def _on_release(self, ev):
        d = self._drag
        self._drag = None
        if not d:
            return
        if d['mode'] == 'nodes' and d['moved']:
            for n in d['orig']:
                self.pinned[n] = list(self.pos[n])
            self._save_pinned()
        elif d['mode'] == 'band':
            self.canvas.delete('band')
            x1, x2 = sorted((d['x'], ev.x))
            y1, y2 = sorted((d['y'], ev.y))
            wx1, wy1 = x1 / self.zoom + self.offx, y1 / self.zoom + self.offy
            wx2, wy2 = x2 / self.zoom + self.offx, y2 / self.zoom + self.offy
            inside = set()
            for n in self.visible:
                if n not in self.pos:
                    continue
                px, py = self.pos[n]
                w, h = self.sizes.get(n, (160, 44))
                if px < wx2 and px + w > wx1 and py < wy2 and py + h > wy1:   # touches the box
                    inside.add(n)
            self.multi |= inside
            self._show_multi_details()
            self.request_redraw()
        elif d['mode'] == 'pan' and not d['moved']:
            self._clear_selection()

    def _on_middle_press(self, ev):
        self._drag = {'x': ev.x, 'y': ev.y, 'moved': False, 'mode': 'pan', 'ox': self.offx, 'oy': self.offy}

    def _on_middle_drag(self, ev):
        self._on_drag(ev)

    def _show_multi_details(self, group_name=None):
        if len(self.multi) < 2:
            if not self.multi:
                self._show_details()
            return
        seg = [(f'{group_name or "Selection"}\n', 'h1'),
               (f'{len(self.multi)} nodes. Drag any of them to move them together; '
                'they stay where you drop them (right-click → Unpin to release).\n\n', 'dim')]
        for n in sorted(self.multi, key=lambda x: self.g.nodes[x].title.lower()):
            seg += [(f'  {self.g.nodes[n].title}', 'link:' + n), (f'  {self.g.nodes[n].subtitle}\n', 'dim')]
        self._show_details(seg)

    def _on_double(self, ev):
        kind, obj = self._item_under(ev.x, ev.y)
        if kind == 'node':
            self.focus_var.set('flow')
            self.select(obj)
            self._refresh(relayout=True)
            self.after(30, self.fit)
        elif kind == 'group':
            members = [n for n in self.g.groups.get(obj, {}).get('nodes', []) if n in self.g.nodes]
            if members:
                self._focus(members[0], 'flow')

    def _on_context(self, ev):
        kind, obj = self._item_under(ev.x, ev.y)
        if kind == 'group':
            members = [n for n in self.g.groups.get(obj, {}).get('nodes', []) if n in self.pos]
            m = tk.Menu(self, tearoff=0)
            m.add_command(label='Focus: this flow', command=lambda: members and self._focus(members[0], 'flow'))
            m.add_command(label='Unpin this flow (back to automatic positions)', command=lambda: self._unpin_many(members))
            m.tk_popup(ev.x_root, ev.y_root)
            return
        if kind == 'node' and obj in self.multi and len(self.multi) > 1:
            m = tk.Menu(self, tearoff=0)
            m.add_command(label=f'Unpin the {len(self.multi)} selected nodes', command=lambda: self._unpin_many(list(self.multi)))
            m.add_command(label='Clear selection', command=self._clear_selection)
            m.tk_popup(ev.x_root, ev.y_root)
            return
        if kind != 'node':
            return
        self.select(obj)
        n = self.g.nodes[obj]
        m = tk.Menu(self, tearoff=0)
        m.add_command(label='Focus: its whole flow', command=lambda: self._focus(obj, 'flow'))
        m.add_command(label='Focus: what it leads to', command=lambda: self._focus(obj, 'down'))
        m.add_command(label='Focus: what leads to it', command=lambda: self._focus(obj, 'up'))
        m.add_command(label='Focus: both', command=lambda: self._focus(obj, 'both'))
        m.add_command(label='Show everything', command=lambda: self._focus(obj, 'all'))
        m.add_separator()
        m.add_command(label='Unpin (back to automatic position)', command=lambda: self._unpin(obj),
                      state='normal' if obj in self.pinned else 'disabled')
        if n.path:
            m.add_command(label='Copy file path', command=lambda: self._copy(n.path))
            m.add_command(label='Show file in folder', command=lambda: self._reveal(n.path))
        if n.guid:
            m.add_command(label='Copy GUID', command=lambda: self._copy(n.guid))
        m.tk_popup(ev.x_root, ev.y_root)

    def _focus(self, nid, mode):
        self.focus_var.set(mode)
        self.select(nid)
        self._refresh(relayout=True)
        self.after(30, self.fit)

    def _unpin_many(self, ids):
        for n in ids:
            self.pinned.pop(n, None)
        self._save_pinned()
        self._refresh(relayout=True)

    def _unpin(self, nid):
        self.pinned.pop(nid, None)
        self._save_pinned()
        self._refresh(relayout=True)

    def _on_wheel(self, ev):
        if ev.delta:
            self._zoom_at(ev.x, ev.y, 1.15 if ev.delta > 0 else 1 / 1.15)

    def _zoom_at(self, sx, sy, factor):
        new = min(3.0, max(0.12, self.zoom * factor))
        wx, wy = sx / self.zoom + self.offx, sy / self.zoom + self.offy
        self.zoom = new
        self.offx, self.offy = wx - sx / new, wy - sy / new
        self.request_redraw()

    def _on_motion(self, ev):
        c = self.canvas
        c.delete('tip')
        if self._drag:
            return
        kind, obj = self._item_under(ev.x, ev.y)
        text = None
        if kind == 'node':
            n = self.g.nodes[obj]
            text = f'{n.title}\n{n.subtitle}' + (f'\n{os.path.relpath(n.path, self.project.root)}' if n.path else '')
        elif kind == 'edge' and obj < len(self.vis_edges):
            e = self.vis_edges[obj]
            text = f'{self.g.nodes[e.src].title} → {self.g.nodes[e.dst].title}\n{e.kind}: {e.label}'
        if text:
            t = c.create_text(ev.x + 14, ev.y + 16, text=text, anchor='nw', fill='#1a202c', font=self._font(9, False), tags='tip')
            bx = c.bbox(t)
            r = c.create_rectangle(bx[0] - 5, bx[1] - 3, bx[2] + 5, bx[3] + 3, fill='#fefcbf', outline='#b7791f', tags='tip')
            c.tag_lower(r, t)

    def select(self, nid, center=False):
        self.selected = nid if (self.g and nid in self.g.nodes) else None
        self.highlight = set()
        self._show_details()
        if self.focus_var.get() != 'all':
            self._refresh(relayout=True)
        if center and self.selected in self.pos:
            x, y = self.pos[self.selected]
            w, h = self.sizes.get(self.selected, (160, 44))
            self.offx = x + w / 2 - self.canvas.winfo_width() / 2 / self.zoom
            self.offy = y + h / 2 - self.canvas.winfo_height() / 2 / self.zoom
        self.request_redraw()

    def _clear_selection(self):
        self.selected = None
        self.highlight = set()
        self.multi = set()
        if self.focus_var.get() != 'all':
            self.focus_var.set('all')
            self._refresh(relayout=True)
        self._show_details()
        self.request_redraw()

    def fit(self):
        if not self.pos:
            return
        ids = [n for n in self.visible if n in self.pos]
        if not ids:
            return
        x1 = min(self.pos[n][0] for n in ids)
        y1 = min(self.pos[n][1] for n in ids)
        x2 = max(self.pos[n][0] + self.sizes.get(n, (160, 44))[0] for n in ids)
        y2 = max(self.pos[n][1] + self.sizes.get(n, (160, 44))[1] for n in ids)
        cw, ch = max(200, self.canvas.winfo_width()), max(200, self.canvas.winfo_height())
        self.zoom = max(0.12, min(1.4, min((cw - 60) / max(1, x2 - x1), (ch - 60) / max(1, y2 - y1))))
        self.offx = x1 - (cw / self.zoom - (x2 - x1)) / 2
        self.offy = y1 - (ch / self.zoom - (y2 - y1)) / 2
        self.request_redraw()

    # ========================================================== side panels
    # ---------------------------------------------------------------- images
    def _photo(self, guid, max_width):
        """PhotoImage for any project image GUID (cached by file timestamp), or (None, reason)."""
        path = self.snap.guid_to_path.get(guid) if self.snap else None
        if not path:
            return None, 'missing file (GUID not found in the project)', None
        try:
            stamp = os.stat(path).st_mtime_ns
        except OSError:
            return None, 'file not found', path
        key = (path, stamp, max_width)
        if key in self._image_cache:
            return self._image_cache[key] + (path,)
        try:
            kind, payload, info = eg_images.load_preview(path, max_width)
            if kind == 'file':
                img = tk.PhotoImage(file=payload)
                factor = max(1, -(-img.width() // max_width))
                if factor > 1:
                    img = img.subsample(factor, factor)
            else:
                img = tk.PhotoImage(data=payload)
            result = (img, info)
        except (eg_images.ImageError, tk.TclError, OSError, ValueError) as e:
            result = (None, str(e))
        self._image_cache[key] = result
        return result + (path,)

    @staticmethod
    def _mirrored(img):
        """Horizontal mirror of a PhotoImage (pure Tk: one 1-px column copy each)."""
        w, h = img.width(), img.height()
        out = tk.PhotoImage(width=w, height=h)
        for x in range(w):
            out.tk.call(out, 'copy', img, '-from', x, 0, x + 1, h, '-to', w - 1 - x, 0)
        return out

    def _insert_image(self, t, keep, guid, fid, max_width, caption, flip=False):
        img, info, path = self._photo(guid, max_width)
        if img is not None and flip:
            img = self._mirrored(img)
        name = os.path.basename(path) if path else f'{guid[:8]}...'
        if img is not None:
            t.image_create('end', image=img, padx=0, pady=3)
            keep.append(img)
            t.insert('end', '\n')
        t.insert('end', f'{caption}\n', 'b' if img is not None else 'warn')
        extra = '' if img is not None else f'  ⚠ {info}'
        sub = ''
        if fid and str(fid) not in ('', str(eg_model.SPRITE_FILEID)):
            sub = f'  (sprite {fid} inside the file; the preview shows the whole image)'
        t.insert('end', f'{name}  ·  {info if img is not None else ""}{extra}{sub}\n', 'dim' if img is not None else 'err')

    def _show_details(self, segments=None):
        t = self.details
        t.configure(state='normal')
        t.delete('1.0', 'end')
        self._shown_images = []
        if segments is None:
            if self.g and self.selected:
                segments = eg_model.details(self.g, self.selected, self.project, self.snap)
            else:
                segments = self._overview_segments()
        for text, tag in segments:
            if tag.startswith('img:'):
                parts = tag[4:].split('|')
                guid, fid, width = parts[0], parts[1], parts[2]
                flip = len(parts) > 3 and parts[3] == 'flip'
                self._insert_image(t, self._shown_images, guid, fid, int(width), text.rstrip('\n'), flip)
                continue
            if tag.startswith('link:'):
                target = tag[5:]
                ltag = 'lnk_' + str(abs(hash(target)))
                t.tag_configure(ltag, foreground='#63b3ed', underline=True)
                t.tag_bind(ltag, '<Button-1>', lambda e, n=target: self.select(n, center=True))
                t.tag_bind(ltag, '<Enter>', lambda e: t.configure(cursor='hand2'))
                t.tag_bind(ltag, '<Leave>', lambda e: t.configure(cursor=''))
                t.insert('end', text, ltag)
            else:
                t.insert('end', text, tag)
        t.configure(state='disabled')

    def _overview_segments(self):
        if not self.g:
            return [('No project loaded.', 'dim')]
        g = self.g
        from collections import Counter
        kinds = Counter(n.kind for n in g.nodes.values())
        cats = Counter(n.category for n in g.nodes.values() if n.kind == 'event')
        seg = [('Event graph\n', 'h1'), ('Read-only view of your project. Click a node for details; drag to move it; '
                                         'double-click to see its whole flow; right-click for more. Drag a flow\'s box to move the whole flow; '
                                         'Shift+drag on empty space (or Ctrl/Shift+click) to select several nodes and move them together.\n\n', 'dim')]
        seg.append(('Events: ', 'b'))
        seg.append((', '.join(f'{v} {k}' for k, v in sorted(cats.items())) + '\n', ''))
        for k in ('table', 'option', 'start', 'dialogue', 'enemy', 'ending'):
            if kinds.get(k):
                seg.append(({'enemy': 'Enemies', 'dialogue': 'Dialogues'}.get(k, k.title() + 's') + ': ', 'b'))
                seg.append((f'{kinds[k]}\n', ''))
        seg.append(('\nLive: ', 'b'))
        seg.append(('the graph re-reads files that change on disk about once a second. Unity writes Inspector '
                    'edits to .asset files when you save (Ctrl+S / File > Save Project), and scene edits when you save the scene.\n', ''))
        if self.snap and self.snap.parse_errors:
            seg.append(('\nCould not read:\n', 'err'))
            for p, msg in self.snap.parse_errors:
                seg.append((f'  {os.path.basename(p)}: {msg}\n', 'err'))
        return seg

    def _fill_banners(self):
        t = self.banner_text
        t.configure(state='normal')
        t.delete('1.0', 'end')
        self._banner_images = []
        g, snap = self.g, self.snap
        lib = getattr(g, 'banner_library', None)
        t.insert('end', 'Banners\n', 'h1')
        t.insert('end', 'Effective BannerLibrary resolution, including event + planet, event + planet-tag, and event-family + planet-tag overrides. '
                        'Select a planet event to see the exact banner it gets on every eligible planet. Swap a PSD and this refreshes.\n\n', 'dim')

        def definition_name(ref):
            gid, _ = eg_model._ref(ref)
            node = g.nodes.get('ev:' + gid) if gid else None
            return node.title if node else (gid[:8] + '...' if gid else '(none)')

        if not lib:
            t.insert('end', 'No BannerLibrary asset in the project.\n', 'warn')
        else:
            t.insert('end', f'Library: {os.path.relpath(lib.path, self.project.root)}\n\n', 'dim')
            d = lib.data
            slots = [('Asteroid default (asteroid + hazard events)', d.get('asteroid')), ('Derelict default', d.get('derelict'))]
            slots += [(f'Planet tag "{eg_model._s(e.get("tag"))}"', e.get('banner')) for e in d.get('planetTags') or []]
            slots += [('Planet fallback', d.get('planetFallback'))]
            slots += [(f'Event {definition_name(e.get("definition"))} + exact planet "{eg_model._s(e.get("planetId"))}"', e.get('banner'))
                      for e in d.get('eventPlanetOverrides') or []]
            slots += [(f'Event {definition_name(e.get("definition"))} + planet tag "{eg_model._s(e.get("planetTag"))}"', e.get('banner'))
                      for e in d.get('eventPlanetTagOverrides') or []]
            slots += [(f'Event family "{eg_model._s(e.get("eventTag"))}" + planet tag "{eg_model._s(e.get("planetTag"))}"', e.get('banner'))
                      for e in d.get('eventTagPlanetTagOverrides') or []]
            slots += [(f'Per-event override: {definition_name(e.get("definition"))}', e.get('banner'))
                      for e in d.get('definitionOverrides') or []]
            for label, ref in slots:
                gid, fid = eg_model._ref(ref)
                t.insert('end', label + '\n', 'h2')
                if not gid:
                    t.insert('end', '  (empty)\n\n', 'dim')
                    continue
                self._insert_image(t, self._banner_images, gid, fid, 300, '')
                uses = [(n, why) for n, why in g.banner_uses.get(gid, []) if n]
                if uses:
                    names = sorted({g.nodes[n].title for n, _ in uses})
                    t.insert('end', f'  used by {len(names)}: ' + ', '.join(names[:12]) + (' ...' if len(names) > 12 else '') + '\n', 'dim')
                t.insert('end', '\n')

        own = []
        for nid, n in g.nodes.items():
            if n.kind != 'event':
                continue
            gid, fid = eg_model._ref(n.data.get('banner'))
            if gid:
                own.append((nid, n, gid, fid))
        if own:
            t.insert('end', 'Events with their own Banner field\n', 'h1')
            for nid, n, gid, fid in sorted(own, key=lambda item: item[1].title.lower()):
                t.insert('end', n.title + '\n', 'h2')
                self._insert_image(t, self._banner_images, gid, fid, 300, '')
                t.insert('end', '\n')

        varying = []
        for nid, n in g.nodes.items():
            b = getattr(n, 'banner', None)
            if n.kind != 'event' or not b or 'planets' not in b:
                continue
            rows = b.get('planets') or []
            signatures = {(gid, why) for _, gid, _, why in rows}
            if len(rows) > 1 and len(signatures) > 1:
                varying.append((nid, n, rows))
        if varying:
            t.insert('end', 'Events whose banner changes by planet\n', 'h1')
            for nid, n, rows in sorted(varying, key=lambda item: item[1].title.lower()):
                t.insert('end', n.title + '\n', 'h2')
                for pid, gid, fid, why in rows:
                    name = os.path.basename(snap.guid_to_path.get(gid, '')) if gid else '(none)'
                    t.insert('end', f'  {pid}: {name}  via {why}\n', 'dim' if gid else 'warn')
                t.insert('end', '\n')

        if snap and snap.planets:
            t.insert('end', 'Normal planet banners\n', 'h1')
            for pl in snap.planets:
                t.insert('end', f'{pl["id"]}  ', 'h2')
                t.insert('end', f'tags: {", ".join(pl["tags"]) or "none"}\n', 'dim')
                gid, fid, why = g.planet_banners.get(pl['id'], (None, None, 'no banner'))
                if gid:
                    self._insert_image(t, self._banner_images, gid, fid, 220, f'  via {why}')
                else:
                    t.insert('end', '  no banner (no matching planet tag in the library and no fallback)\n', 'warn')
                t.insert('end', '\n')
        t.configure(state='disabled')

    def _fill_side_panels(self):
        self._show_details()
        self._fill_banners()
        # flags
        tree = self.flag_tree
        prev = tree.selection()
        prev_key = tree.item(prev[0], 'text') + '|' + str(tree.set(prev[0], 'scope')) if prev else None
        tree.delete(*tree.get_children())
        prefix = self.prefix_var.get().strip()
        for sk in sorted(self.g.state.values(), key=lambda s: (s.key.lower(), s.scope)):
            if prefix and not sk.key.startswith(prefix):
                continue
            iid = tree.insert('', 'end', text=f'{sk.kind}: {sk.key}', values=(sk.scope, len(sk.writers), len(sk.readers)))
            if prev_key == f'{sk.kind}: {sk.key}|{sk.scope}':
                tree.selection_set(iid)
        # checks
        ct = self.check_tree
        ct.delete(*ct.get_children())
        order = {'error': 0, 'warning': 1, 'info': 2}
        self._check_items = {}
        for i in sorted(self.g.issues, key=lambda x: order.get(x.level, 3)):
            iid = ct.insert('', 'end', text=i.text, values=(i.level,), tags=(i.level,))
            self._check_items[iid] = i

    def _flag_selected(self):
        sel = self.flag_tree.selection()
        if not sel or not self.g:
            return
        text = self.flag_tree.item(sel[0], 'text')
        scope = self.flag_tree.set(sel[0], 'scope')
        kind, _, key = text.partition(': ')
        sk = self.g.state.get((kind, scope, key))
        if not sk:
            return
        self.highlight = {n for n, _, _ in sk.writers + sk.readers if n}
        seg = [(f'{sk.key}\n', 'h1'), (f'{sk.kind} · scope {sk.scope}\n\n', 'dim'), ('Set by\n', 'h2')]
        for n, where, val in sk.writers:
            shown = '' if val is None else (' → on' if val else ' → off')
            if n:
                seg += [(f'  {self.g.nodes[n].title}', 'link:' + n), (f'{shown}  ({where})\n', 'dim')]
            else:
                seg.append((f'  {where}\n', 'dim'))
        if not sk.writers:
            seg.append(('  nothing in the data (may be set by code)\n', 'warn'))
        seg.append(('\nRead by\n', 'h2'))
        for n, where, val in sk.readers:
            shown = '' if val is None else (' needs on' if val else ' needs off')
            if n:
                seg += [(f'  {self.g.nodes[n].title}', 'link:' + n), (f'{shown}  ({where})\n', 'dim')]
            else:
                seg.append((f'  {where}\n', 'dim'))
        if not sk.readers:
            seg.append(('  nothing checks it\n', 'dim'))
        self.selected = None
        self._show_details(seg)
        self.notebook.select(0)
        self.request_redraw()

    def _check_selected(self):
        sel = self.check_tree.selection()
        if not sel:
            return
        issue = self._check_items.get(sel[0])
        if issue and issue.node_id and issue.node_id in self.g.nodes:
            if issue.node_id not in self.visible:
                self.focus_var.set('all')
                self.node_vars.get(self.g.nodes[issue.node_id].category or self.g.nodes[issue.node_id].kind, tk.BooleanVar()).set(True)
                self._refresh(relayout=True)
            self.select(issue.node_id, center=True)

    # ================================================================= search
    def _search_changed(self):
        q = self.search_var.get().strip().lower()
        self._search_i = -1
        if not q or not self.g:
            self._search_hits = []
            self.highlight = set()
        else:
            self._search_hits = [nid for nid, n in sorted(self.g.nodes.items(), key=lambda kv: kv[1].title.lower())
                                 if q in n.title.lower() or q in n.subtitle.lower() or q in nid.lower()]
            self.highlight = set(self._search_hits)
        self.request_redraw()

    def _find_next(self):
        if not self._search_hits:
            return
        self._search_i = (self._search_i + 1) % len(self._search_hits)
        nid = self._search_hits[self._search_i]
        if nid not in self.visible:
            self.focus_var.set('all')
            self._refresh(relayout=True)
        self.select(nid, center=True)
        self.highlight = set(self._search_hits)

    # ================================================================ export
    def _export_mermaid(self):
        if not self.g:
            return
        kinds = {k for k, v in self.edge_vars.items() if v.get()} | ({'combat'} if self.edge_vars['spawn'].get() else set())
        text = eg_model.to_mermaid(self.g, [n for n in sorted(self.visible)], kinds)
        path = filedialog.asksaveasfilename(title='Export the current view as Mermaid', defaultextension='.md',
                                            initialdir=self.project.root if self.project else None,
                                            filetypes=[('Markdown', '*.md'), ('Mermaid', '*.mmd'), ('All files', '*.*')])
        if not path:
            return
        if self.project and os.path.abspath(path).startswith(os.path.join(self.project.root, 'Assets')):
            if not messagebox.askyesno('Event Graph', 'That is inside Assets/. Unity would import it as a text asset. Save anyway?'):
                return
        with open(path, 'w', encoding='utf-8') as f:
            f.write('```mermaid\n' + text + '```\n' if path.endswith('.md') else text)
        self._status(f'Exported {len(self.visible)} nodes to {path}')

    # ================================================================ misc
    def _copy(self, text):
        self.clipboard_clear()
        self.clipboard_append(text)

    @staticmethod
    def _reveal(path):
        folder = os.path.dirname(path)
        try:
            if sys.platform.startswith('win'):
                subprocess.Popen(['explorer', '/select,', os.path.normpath(path)])
            elif sys.platform == 'darwin':
                subprocess.Popen(['open', '-R', path])
            else:
                subprocess.Popen(['xdg-open', folder])
        except Exception:
            pass

    def _status(self, text, level='ok'):
        self.status.configure(text=text)

    def _status_live(self, text, level):
        self.live_label.configure(text=text, fg={'ok': '#2f855a', 'error': '#c05621'}.get(level, '#4a5568'))

    def _save_filters(self):
        f = {('n:' + k): v.get() for k, v in self.node_vars.items()}
        f.update({('e:' + k): v.get() for k, v in self.edge_vars.items()})
        f['prefix'] = self.prefix_var.get()
        f['labels'] = self.labels_var.get()
        f['hideLonely'] = self.hide_lonely_var.get()
        f['groups'] = self.groups_var.get()
        self.settings['filters'] = f
        save_settings(self.settings)

    def _save_pinned(self):
        if self.project:
            self.settings.setdefault('pinned', {})[self.project.root] = self.pinned
            save_settings(self.settings)

    def _on_close(self):
        self.settings['geometry'] = self.geometry()
        self._save_filters()
        save_settings(self.settings)
        self.destroy()
