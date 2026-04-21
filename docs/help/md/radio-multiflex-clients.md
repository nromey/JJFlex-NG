# MultiFlex clients

On Flex radios that support MultiFlex, more than one operator can be connected to the same radio at the same time. This is brilliant for clubs, shared shacks, and coaching situations — and it's useful to know who's connected and what they're doing.

## What the MultiFlex dialog shows

Radio menu > MultiFlex Clients opens the dialog. You see each connected client (including yourself) with:

- Station name — the callsign or client name the remote operator registered under.
- Program — what software they're running (SmartSDR, JJ Flex, etc., and version).
- Slices they have open — which slice letters (A, B, C, D depending on radio) are theirs.
- Their connection handle — mostly useful for kick operations.

Your own client is marked so you can tell yourself apart from everyone else.

## Who can be connected

MultiFlex client count is radio-dependent. The FLEX-6600, 6700, 8600, and Aurora AU-520 support multiple GUI clients. Entry-level models may be single-GUI only. The dialog shows whatever clients the radio reports, bounded by the hardware.

## Disconnecting someone else (kick)

If you're the primary operator on a radio and you need to end another client's session — they forgot to disconnect, you need their slice back, whatever — select their entry in the dialog and choose Disconnect. The radio sends a disconnect command; they see their session end cleanly. Be polite; kicks are visible to the kicked.

## Slice ownership

MultiFlex doesn't mean everyone shares every slice. Each client owns the slices they created. If Client A has slice B open, Client C can't tune slice B — it's not theirs. This is usually fine (everyone operates in their own lane) and occasionally inconvenient (you want to hand a slice off). The solution for handoffs is typically to close the slice on the old client and reopen it on the new one.

## What happens when clients join and leave

You'll hear a short announcement when another client connects or disconnects, including their callsign if they've registered one. The announcement is intentionally brief; the full details live in the MultiFlex Clients dialog.

## Heads-up for contests and shared use

Before a contest, it's worth opening the MultiFlex dialog and confirming who's on the radio. Shared shacks sometimes surprise you — a family member or another club member might still be logged in from earlier. Better to notice before you start running the bands.
