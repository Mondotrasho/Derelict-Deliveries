#!/usr/bin/env python3
"""
Event Graph - a read-only, live view of the event / quest data in a Unity
project (Derelict Deliveries). Nothing here ever writes to the project.

GUI (default):
    python event_graph.py                       # remembers the last project
    python event_graph.py --project "C:/Games/DerelictDeliveries"

Headless (no window):
    python event_graph.py --project . --check                 # print checks, exit 1 on errors
    python event_graph.py --project . --mermaid flow.md       # whole graph as Mermaid
    python event_graph.py --project . --mermaid cult.md --focus cult.distress

Needs Python 3.8+ with Tkinter (included with the python.org installers).
No other packages.
"""
import argparse
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import eg_model      # noqa: E402
import eg_project    # noqa: E402


def headless(args):
    project = eg_project.Project(args.project, args.scene)
    if project.scene is None:
        scenes = project.list_scenes()
        preferred = [s for s in scenes if 'Oscars_Test_Movement' in s]
        project.scene = (preferred or scenes or [None])[0]
    project.poll()
    snap = project.load()
    g = eg_model.build(snap, project)
    print(f'Project: {project.root}')
    print(f'Scene:   {project.scene}')
    print(f'{len(g.nodes)} nodes, {len(g.edges)} links')
    if args.mermaid:
        nodes = set(g.nodes)
        if args.focus:
            start = next((nid for nid, n in g.nodes.items() if n.subtitle == args.focus or n.title == args.focus or nid == args.focus), None)
            if not start:
                print(f'--focus: no node called "{args.focus}"')
                return 2
            walk = eg_model.PRIMARY + (('flag',) if args.flags else ())
            nodes = eg_model.reachable(g, start, True, walk, args.prefix or '') | eg_model.reachable(g, start, False, walk, args.prefix or '')
        kinds = set(eg_model.PRIMARY) | ({'flag'} if args.flags else set())
        with open(args.mermaid, 'w', encoding='utf-8') as f:
            f.write('```mermaid\n' + eg_model.to_mermaid(g, sorted(nodes), kinds) + '```\n')
        print(f'Wrote {args.mermaid} ({len(nodes)} nodes)')
    errors = 0
    for level in ('error', 'warning', 'info'):
        items = [i for i in g.issues if i.level == level]
        if level == 'info' and not args.verbose:
            if items:
                print(f'({len(items)} info notes hidden; --verbose to show)')
            continue
        for i in items:
            print(f'{level.upper():8} {i.text}')
        errors += len(items) if level == 'error' else 0
    return 1 if (args.check and errors) else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--project', help='Unity project folder (the one containing Assets/)')
    ap.add_argument('--scene', help='scene path relative to the project, e.g. Assets/Scenes/Oscars_Test_Movement.unity')
    ap.add_argument('--check', action='store_true', help='no window: print the checks; exit code 1 if there are errors')
    ap.add_argument('--mermaid', help='no window: write the graph as Mermaid to this file')
    ap.add_argument('--focus', help='with --mermaid: only this event (id or name) and everything connected to it')
    ap.add_argument('--flags', action='store_true', help='with --mermaid: include flag links (and follow them for --focus)')
    ap.add_argument('--prefix', help="with --flags: only flags starting with this, e.g. 'cult:'")
    ap.add_argument('--verbose', action='store_true', help='with --check: also print info notes')
    args = ap.parse_args()

    if args.check or args.mermaid:
        if not args.project:
            ap.error('--project is required for --check / --mermaid')
        if not os.path.isdir(os.path.join(args.project, 'Assets')):
            ap.error(f'{args.project} has no Assets/ folder')
        sys.exit(headless(args))

    try:
        import eg_gui
    except ImportError as e:
        print('Tkinter is not available in this Python:', e)
        print('On Windows/macOS use the python.org installer; on Linux install python3-tk.')
        sys.exit(1)
    app = eg_gui.App(args.project, args.scene)
    app.mainloop()


if __name__ == '__main__':
    main()
