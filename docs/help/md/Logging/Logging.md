---
keywords:
  - logging
  - QSO
  - log entry
  - Logging mode
  - log file
  - signal reports
---

# Logging Contacts

Use the logging system in JJ Flexible Radio Access to record contacts (QSOs), search earlier entries, and review log statistics. Logging mode provides a radio pane and a log-entry pane. The logging field shortcuts and the new-entry command open a separate, full log entry form.

## Entering and Leaving Logging Mode

Press `Ctrl+Shift+L` to enter Logging mode. This changes the available menus and activates the logging shortcuts. Press the same key again to return to the previous Classic or Modern tuning mode. Moving between controls with `Tab` does not change the operating mode.

While in the main window, press `F6` to move between the radio pane and the log-entry pane. This changes focus within Logging mode; it does not leave the mode.

## Choosing a Log File

In Logging mode, open the **Log** menu and choose **Log Characteristics**, or press `Ctrl+Shift+N`. Use this dialog to choose the log file and its characteristics before recording contacts. If no log file is configured, opening a log entry can bring up Log Characteristics instead of the entry form.

## Recording a Contact

1. In Logging mode, press `Ctrl+N` to start a new entry in the full log entry form. The main window is hidden while this form is open.
2. Enter the other station's callsign. Use `Tab` to move through the form, or use the field shortcuts under "Going to a Log Field".
3. Enter the signal reports and any other details to keep, such as the operator's name, location, grid square or comments.
4. Press `Ctrl+W` to write the entry to the log. After writing a new contact, the form clears for the next one.
5. Press the **OK** button to close the form and return to the main window. Press `Ctrl+Shift+L` there to leave Logging mode when finished.

Use `Ctrl+W` to write the contact before closing the form; the **OK** button closes it without performing that write. The **Cancel** button restores fields changed during this opening of the form. It does not remove an entry already written to the log.

## Going to a Log Field

These are the default field shortcuts in Logging mode. From the main window, a field shortcut opens the full log entry form and requests that field. Within the form, it moves to the field if the chosen log form includes it.

- Press `Alt+C` to go to the callsign field.
- Press `Alt+T` to go to His RST, the readability, signal strength and tone (RST) report sent to the other station.
- Press `Alt+R` to go to My RST, the report received from the other station.
- Press `Alt+N` to go to the name or handle field.
- Press `Alt+Q` to go to the station's location (QTH) field.
- Press `Alt+S` to go to the state or province field.
- Press `Alt+G` to go to the grid-square field.
- Press `Alt+E` to go to the comments field.
- Press `Alt+D` to set the log entry's date and time to the current date and time in Coordinated Universal Time (UTC).

## Opening, Searching and Reviewing the Log

In Logging mode:

- Press `Ctrl+Alt+L` to open the full log entry form at the callsign field. This also hides the main window until the form closes.
- Press `Ctrl+Shift+F` to open the log search.
- Open the **Log** menu and choose **New Entry**, **Write Entry**, **Search Log**, or **Full Log Form** to execute the corresponding command without its shortcut.

To hear log statistics, press `Ctrl+J`, then `Alt+L`.

## Callbook Lookup

Callbook lookup provides station details from an online service. For service setup and lookup instructions, see [Callbook Lookup](CallbookLookup.md).

## Related Topics

- [Callbook Lookup](CallbookLookup.md)
- [Keyboard Reference](KeyboardReference.md)
- [JJ Key Commands](JJKey.md)
