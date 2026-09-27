#!/bin/sh
# Event Graph - read-only live view of the event data.
exec python3 "$(dirname "$0")/event_graph.py" "$@"
