# Callbook Lookup

JJ Flexible Radio Access can look up callsigns in online callbook databases to automatically fill in contact details — a station's name, QTH, and grid square — so you do not have to type them in yourself.

## How to Use It

Press `Ctrl+L` from anywhere in the application to open the station lookup dialog. Type a callsign into the lookup dialog and press Enter to run the lookup.

Alternatively, when you are logging a QSO, enter the callsign in the Call field and JJ Flexible Radio Access will look it up automatically for you.

## Supported Databases

JJ Flexible Radio Access supports callbook lookups through online services, including QRZ.com and HamQTH. You can choose which callbook service to use under **Tools > Settings**, in the Callbook section.

## What Gets Filled In

A successful lookup can fill in any of the following details, depending on what the remote operator has registered:

- **Name** — the operator's name.
- **QTH** — city and state or country.
- **Grid** — the station's Maidenhead grid square.
- **State or Province** — for the log entry's geographic fields.

Not every field is available for every callsign — the answer depends on what the operator has registered with their callbook service.

**Tip:** Callbook lookups require a working internet connection. If you are operating without internet access (for example, on a strictly local network), lookups will not be available, and you will need to enter contact details by hand.
