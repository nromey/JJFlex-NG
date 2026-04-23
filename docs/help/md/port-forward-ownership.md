# Port Forwarding Safety — Ownership Gate

When you change the SmartLink port forwarding settings, the changes are written to the radio itself — not to your local JJ Flexible Radio Access install. Those settings persist on the radio and affect every client that connects to it, including the radio's owner.

JJ Flexible Radio Access includes a safety gate that prevents you from accidentally changing port settings on a radio you do not own. This page explains how that gate works.

## What the Gate Does

When you press **Apply port forwarding** under **Tools > Settings > Network**, JJ Flexible Radio Access first checks whether the radio considers you to be the primary operator at the rig:

- **If the answer is yes** (you are the one pressing physical PTT, usually because you are locally connected at the radio), a confirmation dialog appears. Press Yes to commit the change; press No to cancel. The dialog defaults to No for safety — an accidental press of the Enter key will not change anything.
- **If the answer is no** (you are connected remotely via SmartLink to someone else's radio, for example), JJ Flexible Radio Access refuses to apply the change. You will hear: "Cannot change SmartLink port settings. You must be the primary operator at the radio."

## Why This Gate Exists

Port forwarding settings are radio-persistent state. If you are remotely connected to a friend's radio, you could theoretically change *their* port forwarding settings, which would affect their future SmartLink reachability. That is almost certainly not what you want to do, even by accident.

The ownership gate ensures that:

- You can configure *your* radio freely, whenever you are at it.
- You cannot accidentally break a remote friend's SmartLink setup.
- You cannot accidentally commit a port change with a misclick even on your own radio, because the confirmation dialog catches those accidents.

## Two Layers of Protection

The gate actually combines two independent checks:

1. **Presence check** — "Are you the primary operator at this radio?" — catches the "you are not authorised to do this at all" case.
2. **Confirmation dialog** — "Are you sure you want to do this?" — catches the "authorised, but you pressed the wrong button by accident" case.

Both checks fire on Apply. You need to pass both of them to commit a change.

## What "Primary Operator" Means

The radio itself tells JJ Flexible Radio Access which connected client is currently the primary operator. The answer is determined by whether PTT activity is routed to your client — if the physical microphone plugged into the radio's front panel would send audio to *your* JJ Flexible Radio Access session, then you are the primary operator. Remote SmartLink clients typically are not the primary operator; they are secondary listeners who can tune and operate but do not receive front-panel microphone activity.

## Future Extension

A stricter version of this gate will protect firmware uploads when that feature ships. For a firmware upload, the presence check will require a fresh PTT press within a short window — proving that you are actively present at the radio, not just signed in. Same protection concept, just with a stronger form of enforcement for the higher-stakes operation.

## Related Topics

- SmartLink Networking — The Three Tiers
- SmartLink and Remote Operation
- Connection Troubleshooting
