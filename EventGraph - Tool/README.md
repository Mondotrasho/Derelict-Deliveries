# Event Graph – a live, read-only view of the event data

A Tkinter window that draws every event, quest stage, table, planet option, dialogue and ending in the project, and how they connect. It **redraws by itself** when the files change. It **never writes to the project**: design still happens in the Unity Inspector, and this only shows what's there.

## Setup
- **Requirements:** Python 3.8+ with Tkinter. The python.org installers include it; on Linux install `python3-tk`. **No other packages.**
- **Where to put it:** in your project, **outside `Assets/`**, e.g. `Tools/EventGraph/`. Unity ignores folders outside `Assets`.
- **Run it:** double-click `run_event_graph.bat` (Windows), or `python event_graph.py`. The first time, click **Open project…** and pick the folder that contains `Assets/`. It remembers the project, scene, filters and any nodes you dragged. Those are stored in `~/.event_graph_tool.json`, not in the project.

## What you see
| Node | Shape / colour |
|---|---|
| Event | rectangle, coloured by category (asteroid brown, planet blue, derelict grey, hazard red, pickup yellow) |
| Table (with the source that rolls it) | dark rectangle |
| Planet option | teal pill |
| Quest start (QuestScheduler) | green pill |
| Dialogue JSON | purple |
| Enemy | dark red |
| Ending ("EVENT END n: …" in a result text) | gold pill |

| Link | Meaning |
|---|---|
| grey arrow | a table, planet option or start event leads to this event |
| white arrow | a choice's **follow-up**, in the same window |
| dashed orange | a choice **schedules** this N turns later |
| purple | a choice or option opens this dialogue |
| thick red | spawns or fights this enemy |
| yellow | reaches this ending |
| dashed green (**Story links**) | this event sets a flag/tag/counter **to the value** another one requires, and only a few events touch that key |
| dotted grey (**Other flag links**, off by default) | the same for busy shared keys |

A red or orange dot on a node means a check found an error or warning there.

## Flows, story links and grouping
- **Story links** (dashed green): a flag, tag or counter that **only a few events set and read** is drawn as "this leads to that". For example, `cult:egg` is set only by *Journey's End*, so everything that needs it follows *Journey's End*.
  - Busy or code-set keys (`res.supplies`, `officer:*`, `events.resolved.*`) are **not** story links. They stay faint grey **Other flag links** (off by default) and never affect the layout.
  - "Only a few" means at most 3 setters and 5 readers (`STORY_MAX_WRITERS`, `STORY_MAX_READERS` in `eg_model.py`).
  - Setting a flag *off* never counts as unlocking something that needs it *on*.
- **Flows** (the labelled boxes): events joined by follow-ups, schedules, story links, endings and the planet options or quest starts that open them are one flow. The cult quest is one flow, laid out in story order.
- **What a flow contains:**
  - Tables join the plain content they roll (asteroid, derelict, pickups).
  - Shared dialogues and enemies (the eldritch monster) sit in the flow that uses them most.
  - Loose planet options (Hail, Provoke) share a "Planet options" box.
- **Naming a flow:** it's named after a tag shared by 2+ of its events that isn't a source tag (all cult events carry `cult`, hence "Cult"), otherwise its table, otherwise its entry event.
- **Force a grouping with tags:** add **`group:<Name>`** to events, e.g. `group:Cult Quest`. Every event with the same group tag lands in one flow with that name, even if nothing links them. The game ignores these tags; nothing reads them.
- **Toggle:** **Group into flows** turns the boxes off and falls back to plain connected pieces.

## Banners
Check that every event shows the banner you meant, straight from your PSDs. Resolution is the same as the game's Banner Library:
1. the event's own **Banner** field,
2. the library's per-event overrides,
3. the category default: asteroid (also hazards) or derelict.

**Planet events and planet options** show *the banner of the planet they happen on*, so they list every planet they can appear on, each with the banner that planet's tags pick (first matching planet tag in the library, else the fallback).

- **Details panel:** each event shows its banner picture and **why** it got it ("the event's own Banner field", "planet tag Grey", ...), plus the file name and size.
- **Banners tab:** every library slot with its picture and which events end up using it, then the events with their own banner, then each planet with its tags and banner.
- **Checks:** a banner pointing at a missing file; a planet with no banner (no matching tag, no fallback); a library tag entry with no image, or a tag no planet has; events left blank because a category default is empty.
- **Live:** swap or re-save a PSD and the previews refresh within a second.

**Image formats:**
- **PSD and PSB** need no install: the tool reads the flattened copy Photoshop stores in the file. That copy only exists if **Maximize Compatibility** was on when saving (Photoshop's default); otherwise the preview says so. RGB or greyscale, 8 or 16 bit.
- **PNG and GIF** also need nothing.
- **JPG, TGA and others** need Pillow (`pip install pillow`).
- **Multi-sprite files:** if a banner reference points at one sprite inside a multi-sprite texture, the preview shows the whole file and says so.

## Using it
- **Select:** click a node to see everything about it in **Details**: text, choices, odds as %, resources, flags written, what it leads to, what reaches it, and its checks. Blue names are links.
- **Focus:** double-click any node to see **its whole flow**, e.g. the complete cult quest from any of its stages, plus whatever feeds it (its table, planet options, start event). Right-click also offers *what it leads to*, *what leads to it* and both, and these follow story links automatically.
  - Tick **…also through other flag links** to follow the busy keys too (rarely useful). The prefix box filters which flag links are drawn.
- **Move things:**
  - **One node:** drag it.
  - **A whole flow:** drag its box or label. Double-click the box to focus on that flow.
  - **Several nodes:** **Shift + drag** draws a selection rectangle (it works over flow boxes too). **Ctrl/Shift + click** adds or removes single nodes. Then drag any selected node and they all move together.
  - **Moved nodes stay pinned** where you drop them, remembered per project. Right-click a node, a selection or a flow box to **unpin** it; **Re-layout** unpins everything.
- **Navigate:** drag empty space or use the middle mouse button to pan, the mouse wheel to zoom, **F** to fit, **Esc** to clear the selection.
- **Flags tab:** every flag/tag/counter with where it's set (→ on/off) and where it's read (needs on/off). Selecting one highlights those nodes in green.
- **Checks tab:** facts about the data, sorted by severity; click one to jump to the node.
  - **Errors:** missing references and dialogue files; JSON that won't parse; duplicate event ids.
  - **Warnings:** a flag required but never set; an event nothing leads to; a table entry whose tags its source never rolls; choices with no outcomes or zero weights; dialogue lines with an unknown speaker or a jump to a missing tick.
  - **Info:** set-but-never-read keys, events that still use the placeholder buttons.
- **Find:** type in the box; Enter jumps through the matches.
- **Export Mermaid…:** saves the current view as a Mermaid diagram for the docs.

## "Live" – what that means with Unity
The tool re-reads any file whose timestamp changed, about once a second. Unity only writes changes to disk when it saves:
- **ScriptableObjects** (events, tables): when you save the project (**Ctrl+S** / *File → Save Project*).
- **Scene** (planet options, QuestScheduler, run summary lines): when you save the scene.
- **Dialogue JSON:** as soon as your editor saves the file.

Nodes whose file just changed flash orange for a moment. If Unity is halfway through writing a file, the last good version stays on screen and the next poll picks up the finished one.

## Without the window
```
python event_graph.py --project . --check                    # print checks; exit code 1 on errors
python event_graph.py --project . --check --verbose          # include info notes
python event_graph.py --project . --mermaid docs/cult.md --focus cult.distress --flags --prefix cult:
```
`--check` fits next to `doc_hash_check.py`, e.g. before committing.

## Limits (on purpose)
- **Read-only:** no editing, no suggestions, no generated content.
- **Saved files only:** it reads what's on disk, not Unity's memory; save in Unity to see changes.
- **Code-set keys:** keys the game's code sets by itself (`events.resolved.*`, `poi:*:done`, `event:available`, `res.supplies`) are known to it, so they don't show as "never set". If new code starts setting keys, add their prefix to `CODE_WRITTEN_PREFIXES` in `eg_model.py`.
- **Planet colour tags** (Green, Red, …) are read by the Banner Library, which the tool doesn't model, so they aren't reported as unused.

## Files
| File | Job |
|---|---|
| `event_graph.py` | entry point (window or headless) |
| `eg_yaml.py` | reads Unity's YAML. Its own small parser: Unity writes values like `debugText: Placeholder hazard: small rocks…` that strict YAML libraries reject |
| `eg_project.py` | scans `Assets/`, resolves GUIDs through `.meta` files, caches by file timestamp, keeps the last good read |
| `eg_model.py` | builds the graph, the flag index and the checks; Mermaid export; details text |
| `eg_layout.py` | deterministic left-to-right layout (the same data always gives the same picture) |
| `eg_images.py` | banner previews: its own PSD reader (composite image, RLE / raw, 8 / 16-bit), PNG / GIF via Tk, others via Pillow if installed |
| `eg_gui.py` | the Tkinter window |
