"""
Layered left-to-right layout for the Event Graph tool.

Deterministic on purpose: the same data always gives the same picture, so a
live reload after a small edit moves as little as possible. Nodes the user has
dragged are pinned and keep their position.

1. Split into connected groups (flag links ignored, they would glue everything).
2. Rank each group by longest path from its roots (cycles broken by DFS).
3. Order each rank by the average position of its neighbours (a few sweeps).
4. Columns sized by their widest node; groups stacked top to bottom.
"""
from collections import defaultdict

KIND_ORDER = {'start': 0, 'table': 1, 'option': 2, 'event': 3, 'dialogue': 4, 'enemy': 5, 'ending': 6}


def layout(nodes, edges, sizes, pinned=None, col_gap=90, row_gap=26, group_gap=70, aspect=1.6,
           groups=None, header=34, pad=16, group_min_width=None):
    """nodes: {id: Node}; edges: [(src, dst)]; sizes: {id: (w, h)}.
    Groups are shelf-packed towards the given width/height aspect.
    Returns {id: (x, y)} top-left positions."""
    pinned = pinned or {}
    ids = sorted(nodes, key=lambda n: (KIND_ORDER.get(nodes[n].kind, 9), nodes[n].title.lower(), n))
    out_e, in_e, und = defaultdict(list), defaultdict(list), defaultdict(set)
    for a, b in edges:
        if a in nodes and b in nodes and a != b:
            out_e[a].append(b)
            in_e[b].append(a)
            und[a].add(b)
            und[b].add(a)

    # 1. groups: the flows from the model if given (each drawn as a labelled box),
    #    otherwise connected pieces of the graph.
    groups_param = groups
    if groups:
        by = {}
        for n in ids:
            by.setdefault(groups.get(n, n), []).append(n)
        comps = sorted(by.values(), key=lambda c: (len(c) == 1, -len(c), min(nodes[x].title.lower() for x in c)))
    else:
        comps = None
    groups, seen = ([], set()) if comps is None else (comps, set(ids))
    for n in ids:
        if n in seen:
            continue
        comp, stack = [], [n]
        seen.add(n)
        while stack:
            cur = stack.pop()
            comp.append(cur)
            for nb in sorted(und[cur]):
                if nb not in seen:
                    seen.add(nb)
                    stack.append(nb)
        groups.append(comp)
    # big, entry-led groups first; lone nodes last
    if comps is None:
        groups.sort(key=lambda c: (len(c) == 1, -len(c), min(KIND_ORDER.get(nodes[x].kind, 9) for x in c), min(nodes[x].title.lower() for x in c)))

    placed = []                 # (local positions, width, height) per group
    for comp in groups:
        cset = set(comp)
        order_key = {n: i for i, n in enumerate(ids)}
        comp.sort(key=lambda n: order_key[n])

        # 2. break cycles (DFS back edges) then longest-path ranks
        back, state = set(), {}
        def dfs(u):
            state[u] = 1
            for v in sorted(out_e[u], key=lambda x: order_key.get(x, 0)):
                if v not in cset:
                    continue
                if state.get(v) == 1:
                    back.add((u, v))
                elif v not in state:
                    dfs(v)
            state[u] = 2
        roots = [n for n in comp if not [p for p in in_e[n] if p in cset]] or comp[:1]
        for r in roots:
            if r not in state:
                dfs(r)
        for n in comp:
            if n not in state:
                dfs(n)

        rank = {n: 0 for n in comp}
        # Kahn-style longest path on the DAG
        indeg = {n: 0 for n in comp}
        for u in comp:
            for v in out_e[u]:
                if v in cset and (u, v) not in back:
                    indeg[v] += 1
        queue = [n for n in comp if indeg[n] == 0]
        while queue:
            u = queue.pop(0)
            for v in out_e[u]:
                if v in cset and (u, v) not in back:
                    rank[v] = max(rank[v], rank[u] + 1)
                    indeg[v] -= 1
                    if indeg[v] == 0:
                        queue.append(v)

        # 3. order within ranks (barycentre sweeps)
        layers = defaultdict(list)
        for n in comp:
            layers[rank[n]].append(n)
        max_rank = max(layers) if layers else 0
        for r in layers:
            layers[r].sort(key=lambda n: order_key[n])
        index = {n: i for r in layers for i, n in enumerate(layers[r])}
        for sweep in range(4):
            rng = range(1, max_rank + 1) if sweep % 2 == 0 else range(max_rank - 1, -1, -1)
            for r in rng:
                def bary(n):
                    nbs = [p for p in (in_e[n] if sweep % 2 == 0 else out_e[n]) if p in cset and rank.get(p) is not None and rank[p] != r]
                    return sum(index[p] for p in nbs) / len(nbs) if nbs else index[n]
                layers[r].sort(key=lambda n: (bary(n), order_key[n]))
                for i, n in enumerate(layers[r]):
                    index[n] = i

        # 4. coordinates
        col_x, x = {}, 0.0
        for r in range(max_rank + 1):
            col_x[r] = x
            widest = max((sizes.get(n, (160, 44))[0] for n in layers.get(r, [])), default=160)
            x += widest + col_gap
        col_h = {r: sum(sizes.get(n, (160, 44))[1] + row_gap for n in layers.get(r, [])) for r in layers}
        band = max(col_h.values(), default=0)
        local = {}
        for r, members in layers.items():
            y = (band - col_h[r]) / 2
            for n in members:
                local[n] = (col_x[r], y)
                y += sizes.get(n, (160, 44))[1] + row_gap
        if comps is not None:        # room for the group's label and box padding
            local = {n: (lx + pad, ly + header) for n, (lx, ly) in local.items()}
            width = x - col_gap + 2 * pad
            minw = (group_min_width or {}).get(groups_param.get(comp[0]), 0) if groups_param else 0
            placed.append((local, max(width, minw), band - row_gap + header + pad))
        else:
            placed.append((local, x - col_gap, band - row_gap))

    # shelf packing: rows of groups, row width limited so the whole picture is roughly `aspect` wide
    total_area = sum((w + group_gap) * (h + group_gap) for _, w, h in placed) or 1
    widest = max((w for _, w, _ in placed), default=0)
    row_limit = max(widest, (total_area * aspect) ** 0.5)
    pos, x, y, row_h = {}, 0.0, 0.0, 0.0
    for local, w, h in placed:
        if x > 0 and x + w > row_limit:
            x, y, row_h = 0.0, y + row_h + group_gap, 0.0
        for n, (lx, ly) in local.items():
            pos[n] = (x + lx, y + ly)
        x += w + group_gap
        row_h = max(row_h, h)

    for n, p in pinned.items():
        if n in pos:
            pos[n] = tuple(p)
    return pos
