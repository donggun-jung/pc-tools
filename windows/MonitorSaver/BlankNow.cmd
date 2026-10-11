@echo off
rem Trigger the blank+lock immediately (same as pressing the hotkey).
rem Useful from a script, e.g. at the end of an overnight batch.
"%~dp0MonitorSaver.exe" -blank
