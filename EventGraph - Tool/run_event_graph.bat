@echo off
rem Event Graph - read-only live view of the event data. Double-click to run.
python "%~dp0event_graph.py" %*
if errorlevel 1 pause
