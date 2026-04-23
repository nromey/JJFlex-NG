# MultiFlex Clients

On Flex radios that support MultiFlex, more than one operator can be connected to the same radio at the same time. This is excellent for clubs, shared shacks, and coaching situations — and it is useful to know exactly who is connected, and what they are doing.

## What the MultiFlex Clients Dialog Shows

Open the **Radio** menu and choose **MultiFlex Clients** to open the dialog. You will see each connected client (including yourself) along with:

- **Station name** — the callsign or client name the remote operator registered under.
- **Program** — the software the remote operator is running (SmartSDR, JJ Flexible Radio Access, and so on), along with its version number.
- **Slices they have open** — which slice letters (A, B, C, D, depending on your radio model) are owned by that client.
- **Connection handle** — mostly useful for kick operations (see below).

Your own client is marked in the list so you can tell yourself apart from everyone else who is connected.

## Who Can Be Connected at Once

The MultiFlex client count is radio-dependent. The FLEX-6600, FLEX-6700, FLEX-8600, and Aurora AU-520 all support multiple GUI clients simultaneously. Entry-level models may be single-GUI only. The MultiFlex Clients dialog always shows whatever clients your radio reports, bounded by the hardware limit.

## Disconnecting Someone Else (Kicking)

If you are the primary operator on the radio and you need to end another client's session — maybe they forgot to disconnect, or you need their slice back — select that client's entry in the MultiFlex Clients dialog and choose Disconnect. The radio sends a disconnect command to that client, and their session ends cleanly. Be polite about this; kicks are visible to the kicked.

## Slice Ownership

MultiFlex does not mean that everyone shares every slice. Each client owns the slices they created. If Client A has Slice B open, Client C cannot tune Slice B — it is not theirs to tune. This is usually fine (everyone operates in their own lane) and occasionally inconvenient (you want to hand a slice off to another operator). The standard handoff workflow is for the old client to close the slice, and then for the new client to reopen it on their own side.

## What Happens When Clients Join and Leave

You will hear a short announcement whenever another client connects or disconnects — including their callsign if they registered one. The announcement is deliberately brief; the full details live in the MultiFlex Clients dialog.

## Heads-Up for Contests and Shared Use

Before a contest, it is worth opening the MultiFlex Clients dialog and confirming exactly who is on the radio. Shared shacks sometimes surprise you — a family member or another club member might still be logged in from an earlier session. Better to notice that before you start running the bands.
