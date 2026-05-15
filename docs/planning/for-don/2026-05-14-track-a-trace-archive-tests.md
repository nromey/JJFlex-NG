# JJ Flex Test — Trace Archive (Track A)

Hey Don,

The latest JJ Flex test build has a new way of keeping radio session traces.

Until now, every JJ Flex session overwrote the same trace file — so if
something interesting happened in your last connection, it was already gone
by the next time we asked you about it. Annoying for both of us.

The new build keeps every session's trace in its own little compressed file.
That means we can ask you about a specific connect attempt from three days
ago and you can actually go back and find it. Plus, the file's name carries
a tag that says whether the session ended cleanly, with an AS retry, or
because JJ Flex got force-quit — useful triage info before we even open the
trace.

This batch is the part of the Sprint 29 test matrix that needs your radio.
Most of them are quick — connect, disconnect, glance at a folder, maybe a
force-quit. Should fit in one sitting.

## Where the archive lives

`%AppData%\JJFlexRadio\Traces\` — paste that into File Explorer's address
bar and it'll take you there. Each session lands as a small compressed file
with the date and an outcome tag in the name. You don't need to open the
files themselves — just confirming they show up is enough for most tests.

The same archive is also visible from inside JJ Flex via
**Operations → Tracing**, on the **Archive Browser** tab. That's the list
your screen reader will navigate for tests 6 and 7.

## Tests

1. **Connect to your radio normally, then disconnect via the Radio menu.**
   Open the Traces folder. A new entry should appear with today's date and
   a "Success" tag in the name.

2. **Connect to your radio. Then force-quit JJ Flex via Task Manager — end
   the process, don't disconnect first.** Reopen JJ Flex. Open the Traces
   folder. The most recent entry should be tagged "Killed" or similar, not
   "Success."

3. **Force an AS-retry condition if you can.** Easiest way is to be already
   connected to your slice from another SmartSDR or JJ Flex session, then
   try to connect a second JJ Flex against the same slice. The retry happens
   automatically. Open the Traces folder. The entry should be tagged
   "AS retry failed" or "Retry succeeded" — either is fine, we just want a
   marker present. If you can't easily reproduce this, write
   `**** SKIP couldn't reproduce` and move on.

4. **Close JJ Flex completely, then reopen it.** Go to Operations → Tracing
   → Archive Browser tab. The entries from tests 1, 2, and 3 should all
   still be there.

5. **In the Archive Browser tab, press the Prune Now button.** It'll ask
   you to confirm. Default retention is 30 days. Confirm, and listen for
   the result announcement. Entries older than 30 days should be removed;
   today's entries should be untouched. (If you don't have anything older
   than 30 days, just confirm the command runs without error.)

6. **Arrow through the Archive Browser list.** Your screen reader should
   read each entry's date and outcome label intelligibly — human strings
   like "AS retry failed," not raw enum names like "AsRetryFailed."

7. **Switch screen readers (JAWS to NVDA or vice versa) and repeat test 6.**
   Flag any difference in how the entries read between the two readers.

## Results

1. **** 

2. **** 

3. **** 

4. **** 

5. **** 

6. **** 

7. **** 

When you're done (or you've gotten as far as you have time for), send this
file back the usual way and we'll fold the results into the Sprint 29
matrix. As always, any test you can't run, write
`**** SKIP <reason>` and move on — they're independent.

73,
JJ Flex (via Noel)
