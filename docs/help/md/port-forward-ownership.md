# Port Forwarding Safety — Ownership Gate

When you change SmartLink port forwarding settings, the changes are written to the radio itself, not to your local JJ Flex install. Those settings persist on the radio and affect every client that connects to it — including the radio's owner.

JJ Flex prevents you from accidentally changing port settings on a radio you don't own. Here's how that works.

## What the Gate Does

When you press **Apply port forwarding** in Settings > Network, JJ Flex first checks whether the radio considers you the primary operator at the rig:

- **If yes** (you're the one pressing physical PTT, usually because you're locally connected at the radio), a confirmation dialog appears. Press Yes to commit, No to cancel. The dialog defaults to No for safety — accidental Enter key presses won't change anything.
- **If no** (you're connected remotely via SmartLink to someone else's radio, for example), JJ Flex refuses to apply the change. You hear: "Cannot change SmartLink port settings. You must be the primary operator at the radio."

## Why This Exists

Port forwarding settings are radio-persistent state. If you're remotely connected to a friend's radio, you could theoretically change THEIR port forwarding, which would affect their future SmartLink reachability. That's almost certainly not what you want to do, even by accident.

The gate ensures:

- You can configure YOUR radio freely when you're at it.
- You can't accidentally break a remote friend's SmartLink setup.
- You can't accidentally change port settings by a misclick even on your own radio, because the confirmation dialog catches accidental commits.

## Two Layers of Protection

The gate actually combines two independent checks:

1. **Presence check** — "are you the primary operator?" — catches "not authorized to do this at all."
2. **Confirmation dialog** — "are you sure?" — catches "authorized, but pressed the wrong button by accident."

Both fire on Apply. You need to pass both to commit a change.

## What "Primary Operator" Means

The radio itself tells JJ Flex which connected client is currently the primary operator. This is determined by whether PTT activity is routed to your client — if the physical mic plugged into the radio's front panel would send audio to YOUR JJ Flex session, you're the primary operator. Remote SmartLink clients typically aren't; they're secondary listeners who can tune and operate but don't receive front-panel mic activity.

## Future Extension

A stricter version of this gate will protect firmware uploads when that feature ships. For firmware, the presence check will require a fresh PTT press within a short window — proving you're actively present at the radio, not just signed in. Same protection concept, stronger enforcement for higher-stakes operations.

## Related Topics

- SmartLink Networking
- Networking Tiers Overview
- Connection Troubleshooting
