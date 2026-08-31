# Audio Troubleshooting

Silence is the hardest thing to diagnose by ear, because the evidence is
missing. This page walks the causes in the order they actually bite, starting
with the one that catches almost everybody the first time.

If you would rather have JJ Flexible Radio Access check for you, open the Audio
Workshop, go to **Diagnostics**, and press **Test My Receive Audio**. It walks
the same ladder against your radio right now, speaks the first thing it finds
wrong, and finishes by telling you whether sound is actually arriving from the
radio — measured from what the radio has been sending, not read back from a
setting. See "Is Sound Actually Arriving From the Radio?" below.

The same page answers the opposite question. **Test My Transmit Chain** walks
your transmit path from the microphone to the antenna and reports the first
stage that is dead, along with an honest account of how much of the chain it
could not see — because a check that could not be made is not a check that
passed. Underneath it sits an evidence block you can copy straight into an
email to Flex support, with every reading, its units, its age, and your radio's
model, serial and firmware already in it.

## Start Here: A Flex Is Silent Until Something Connects To It

If your radio is powered on, headphones are plugged into its front jack, and
you hear nothing at all — that is normal. It is not a fault, and it is not
something you have set wrong.

A Flex is not a conventional transceiver with a receiver wired to a speaker.
It is a server. It does not produce audio at any of its outputs — headphone
jack, line out, or front panel speaker — until a client program connects to it
and asks for a receiver. Powering the radio on gets you a radio that is awake
and listening for a connection. That is all.

So the first question is never "what is muted?" It is **"am I connected?"**
Press `Ctrl+Shift+S` to hear your connection status. If you are not connected,
connect first, and then come back to the rest of this page if you still hear
nothing.

This one surprises people coming from a conventional rig, and it is worth
saying plainly: plugging headphones into an unconnected Flex and hearing
silence tells you nothing is broken.

## Then: Are the Radio's Outputs Muted or Turned Down?

On a radio without a front panel — every non-M model — there is no volume knob
to reach for. The software levels are not one control among several. They are
the only volume control that exists, and if they are at zero, the radio is
silent and nothing on the radio itself will tell you.

Open Settings and go to the Audio tab. Under **Radio Outputs** you will find:

- **Headphone level** and **Line out level**, from 0 to 100. Arrow up and down
  to change them; they take effect on the radio immediately, so you can find
  the right level by listening rather than by saving and reopening. Hold Shift
  with the arrows for single steps.
- **Mute the headphone output**, **Mute the line out output**, and **Mute the
  front panel speaker**. Each one speaks its new state when you toggle it.

These controls appear only when a radio is connected, because they read and
write live radio state. If you do not see them, go back to the section above.

You can also reach the levels without opening Settings: press `Ctrl+Shift+U`
for the Audio expander and arrow to Headphone Level or Line Out Level.

## Then: Is Radio Audio Coming Through Your Computer?

There are two completely different ways to hear a Flex, and mixing them up is a
common source of confusion.

**At the radio.** Headphones or speakers plugged into the radio itself. This
works when you are sitting with the radio, and it needs nothing from your
computer's sound hardware.

**Through your computer.** JJ Flex streams the radio's receive audio to your
computer's speakers and sends your computer's microphone back to the radio.
This is what the **Play radio audio through this computer** checkbox on the
Audio tab controls.

On a remote (SmartLink) connection, the second one is the only way to hear
anything. The radio may be in another state; its headphone jack is no help to
you. JJ Flex turns this on for you automatically when you connect remotely.

Each radio also remembers this setting between sessions now. The **When this
radio connects** choice, right under the checkbox, has three settings: as I
left it (the default), always on for this radio, or always off. As I left it
is the wording you hear at connect too — "PC audio on, as you left it."
Always on is the remote operator's insurance — even if a session ends
with PC audio off, the next connect turns it back on. Whatever happens at
connect is announced out loud, so the switch is never flipped silently.

If the checkbox is off on a remote connection, turn it on. JJ Flex will say
what it did.

## Is Sound Actually Arriving From the Radio?

Everything above this point is a setting. Settings can all be correct while no
sound has ever reached your computer, and no amount of checking them will tell
you that — which is why the receive check now ends with a measurement instead
of only a verdict.

Press **Test My Receive Audio** in the Audio Workshop's Diagnostics page and
the last thing it tells you is what actually arrived, in words like this:

"Audio arriving from the radio: up to 42 kilobits per second, in every one of
10 readings taken about a second apart, counted from the first reading that
carried audio. All data arriving from the radio over the same readings: up to
61 kilobits per second, of which meter readings — the radio reporting its own
gauges — were up to 5. Those figures are measured for comparison: data or
meters still arriving while audio is not would mean the radio is talking to
this computer but not sending sound — a different problem from a dead link."

That is counted from packets the radio sent that crossed your network, so it is
a fact about your radio rather than a claim about this application. It is also
the first thing Flex support will ask you about, and it is now in the evidence
block you can copy to them.

Four answers are worth knowing how to read.

**Sound is arriving.** Any figure above zero, arriving in every reading, means
the audio stream is alive and doing its job. If you still hear nothing, the
problem is on your computer — your sound device, your Windows volume, or the
wrong output chosen — and not between you and the radio.

**Sound is arriving, but it went missing in some readings.** The count starts
at the first reading that carried audio, so blanks at the very start — the
moment between connecting and the stream beginning — are never held against a
healthy radio. Audio missing *after* it began is different: those readings are
the drop-outs you may be hearing, and on a remote connection they usually
point at a weak or congested network rather than at the radio or at this
application. This is the line that tells a network problem apart from a radio
problem, which until now you had no way to do from your chair.

**None is arriving, and none is expected.** If **Play radio audio through this
computer** is off, no sound is meant to come across the network at all, and the
check says so in as many words. You are listening at the radio, nothing is
wrong, and a zero here is the correct answer.

**None is arriving while it should be.** If that setting is on and nothing is
coming through, the check says what else was arriving, because that is what
narrows it down. Meter readings arriving without sound means the radio is
talking to your computer perfectly well and only the audio stream is empty —
switch **Play radio audio through this computer** off and on again, which asks
the radio for a fresh audio stream, and disconnect and reconnect if that does
not do it. Nothing arriving at all, of any kind, points at the connection
between your computer and the radio rather than at anything to do with audio.

One honest note about timing: the readings start when you connect, so for the
first second or two after connecting there is nothing to report yet, and the
check says that rather than reporting a zero. Run it again a few seconds later.
The count works the same honest way once readings exist: any taken before the
audio stream started are left out, and the report says so — a first run after
connecting is never made to look short by blanks that mean nothing.

## Then: Are the Right Sound Devices Chosen?

Radio audio through your computer needs two devices: one to play the radio's
receive audio, and one microphone to send back to the radio. Choose them in
Settings, Audio tab, **Audio Devices** button — or from the Audio menu, where
the same dialog is called Audio Devices.

That dialog covers everything in one place: the audio system, the radio's
playback device, the microphone sent to the radio, the transmit audio quality,
the device your alerts and CW notifications play through, and the meter tone
device. The current choice is announced when the dialog opens, and the system
default is marked in words.

Both device lists are in name order, and a number inside a name is treated as a
number — so Line 2 comes before Line 10, and an interface's numbered inputs sit
together. Typing the first letter of a device jumps you to it, and repeating the
letter walks through everything that starts with it.

Under most controls on that page there is a short line describing what is
happening right now: which device is in use, why a control is greyed, what the
audio system you chose means for the two lists. Those lines are read out when
the dialog opens, and they are not Tab stops — so getting from the audio system
combo to the device list is one press, not two. To hear one again on purpose,
put focus on the control it belongs to and press **Ctrl+F1**: you get what that
control does, followed by whatever its line currently says.

### The Audio System, at the Top of the Dialog

Windows hands the same sound card to programs through more than one driver
model, and JJ Flexible Radio now asks you which one to use rather than guessing. The
choice is the **Audio system** combo at the top of the Audio Devices dialog. It
applies to your microphone and your receive audio together, and the two lists
underneath show whatever that system offers.

**WASAPI is the default, and it is the one that tells you the truth.** It
reports the rate your hardware is really running at, and it refuses a device
that cannot do what the radio needs instead of quietly converting behind your
back.

**MME is the forgiving one.** It converts sample rates for you, so a device
WASAPI turns down will usually work under MME. The cost is that it reports 48
kHz for absolutely everything, so you cannot tell from inside JJ Flexible Radio what
rate your hardware is actually running at. It also cuts device names short at
31 characters, which is a Windows limitation rather than ours.

**DirectSound** sits between the two: it converts rates like MME without the
truncated names.

If a device you own will not open under WASAPI, try MME. If your transmit audio
is behaving strangely and everything looks fine, MME may be the reason you
cannot see the problem — switch to WASAPI and read what the device list says
about that device's rate.

One rescue worth knowing about: some devices are locked to 44.1 kHz — a rate
radio audio cannot use — and under WASAPI they used to be simply unusable. Now,
when a device refuses every rate JJ Flexible Radio can work with, it asks Windows to
convert as a last resort, and the device opens anyway. Every device that can
run without conversion still does, and when the rescue engages, the diagnostic
log says plainly that Windows is resampling and how to get native audio back
(set the device to 48000 Hz in Windows Sound settings). Honesty preserved,
device usable.

The old behaviour, for anyone comparing notes with an earlier version: JJ
Flexible used to fold every copy of a device into one row and pick a driver
model for you. That is what this control replaces, and it is why the device
lists are no longer full of duplicates.

A few more things worth knowing:

- **If a device you chose is unplugged**, JJ Flex falls back to your system
  default and says so out loud. It does not go quiet, and it does not silently
  bind to whatever device happened to take the missing one's place. Your
  original choice stays saved, so plugging the device back in picks it up
  again.
- **Moving a USB headset to a different port is fine.** JJ Flex identifies your
  saved devices by name, not by position in the list, so a reshuffle rebinds
  silently and correctly.
- **Each device appears once.** Windows offers most sound hardware several
  times over, once for each audio system it supports, so a single USB interface
  can arrive as three or four identical-looking choices. Because you have
  already chosen the audio system at the top of the dialog, only that system's
  copy is listed. If you want to see every one of them at once, tick **Show
  every sound endpoint** at the bottom; that view names the audio system after
  each entry, and it is also how you put your microphone on one audio system
  and your receive audio on another if you ever need to.
- **A device that is unplugged says so in the list.** It sits at the top marked
  "Not connected" rather than vanishing. Leave it selected if you plan to plug
  it back in and JJ Flex keeps it saved for you; pick something else and JJ Flex
  switches.
- **Entries marked "loopback" are not microphones.** They are whatever your
  computer is currently playing, offered back as a recording source. Choosing
  one as your transmit microphone would put your own received audio on the air.
  They are labelled so you can tell them apart.
- **Mono devices work.** A great many USB headset microphones have exactly one
  channel, and JJ Flexible Radio used to list them and then refuse them, which meant
  that if it was the only microphone you owned you could not use the app at
  all. That is fixed. A mono microphone is sent to the radio on both channels,
  and a mono speaker gets both channels mixed together. The row says which so
  you know what is happening to your audio, but there is nothing to work
  around any more.
- **Devices with more than two channels work normally.** Many laptop microphone
  arrays report four; JJ Flexible Radio lists them and uses them in stereo. The
  dialog notes it when your chosen device is one of these.
- **A device running at the wrong rate says so in its row.** Audio to and from
  the radio is carried by the Opus codec, which works at 48, 24, 16, 12 or 8
  kHz — and notably not at 44.1 kHz, which is what a good number of sound
  devices sit at by default. When you are on WASAPI, JJ Flexible Radio can see the
  real rate and marks any device that cannot carry radio audio. The cleanest
  fix is to set the device to 48000 Hz in Windows Sound settings; switching
  the audio system to MME also works, since MME converts the rate for you.
  And if you do neither, the device still opens: JJ Flexible Radio falls back to
  asking Windows to convert, as a last resort, rather than leaving the device
  unusable. You will not see the rate warning under MME, because under MME the
  rate JJ Flexible Radio is told is not the rate your hardware is running at.
- **The device list is a snapshot.** If you plug something in while the dialog
  is open, press the **Refresh device list** button.

### Transmit Audio Quality

Under the microphone list is a **Transmit audio quality** setting. Full quality
is the default and the tested setting — leave it there unless your connection
cannot carry it.

The lower settings encode your voice at a lower sample rate, which uses less of
your connection and sounds duller. They exist for the bad night: a remote link
that keeps breaking up on transmit, where duller audio that gets through beats
better audio that does not.

Two honest caveats. Your sound card has the last word — if it cannot run at the
rate you asked for, JJ Flexible Radio opens at a rate it can and encodes to match,
rather than sending the radio something it cannot follow. And because MME
converts rates and WASAPI does not, the lower settings are most likely to
actually take effect while you are on MME. The change applies from your next
connection, not to a connection already running.

## The Radio Cannot Hear Me

You do not have to transmit to find out whether your microphone works. The
Audio Devices dialog has a **Microphone check** right below the microphone
list. Pick your microphone, press **Start microphone check** (Alt+M), and talk.
Nothing is transmitted, the radio is not involved, and no other program has to
be running.

The reading sits just under the button in a read-only box you can Tab to, so
your screen reader's read-current-control command speaks your level whenever
you ask for it. It refreshes about twice a second while the check runs and uses
the same words as the Audio Workshop. Every answer opens with one word that
tells you the whole story — *Good*, *Hot*, *Clipping*, *Quiet*, *Very quiet*,
*Faint*, *Nothing* — followed by what to do about it and the number in dBFS.
The leading word is there so you can stop listening as soon as you have what
you needed.

While you are adjusting, keep the order of the two gain stages in mind:
**capture first, sculpt second.** When your microphone comes through this
computer, the level Windows captures it at is stage one, and everything the
radio does to it — mic gain, compander, processor — is stage two. Get a clean,
Good-verdict capture at the Windows input level before you reach for the
radio's knobs; no amount of stage-two sculpting can repair audio that arrived
clipped or faint at stage one.

Three answers mean three different things, and the check tells them apart:

- **A level that moves when you talk.** Your microphone works. If the verdict
  is "turn it up", reach for the gain knob on your interface first, then Mic
  Gain in the Audio Workshop.
- **Only the electrical noise floor.** JJ Flex hears the interface but nothing
  is arriving at it — a very low number, around -90 dBFS or below, that never
  moves. Check the microphone is plugged into the right input and that the
  interface's own gain is turned up. On an interface with phantom power, a
  condenser microphone with 48V switched off reads exactly like this.
- **No sound at all.** Not quiet — literally nothing, every sample zero. A
  working microphone always has some noise on it, so this means Windows is
  handing JJ Flex silence rather than audio. Look for a mute: on the device
  itself, on the Windows recording device, or in Windows privacy settings.

If Windows privacy settings are what is blocking you, JJ Flex says so by name
and offers an **Open Windows microphone privacy settings** button that takes
you straight to the right page. Turn microphone access on there — including
**Let desktop apps access your microphone**, which is the switch that governs
JJ Flex — then come back and run the check again. If nothing is blocked, that
button never appears.

The check closes the microphone the moment you stop it, switch to a different
device, refresh the list, or close the dialog. It never keeps hold of your
microphone behind your back.

## Alerts and CW Notifications Are Not Playing

Alerts, CW notifications, and meter tones are JJ Flex's own sounds, not the
radio's, and they go out through a different device than radio audio does. If
you can hear the radio clearly but not the beeps, the alert device is the one
to check — Settings, Audio tab, **Alert device**.

CW notifications play through that same alert device. There is no separate CW
output to choose, which is why the **Enable CW notifications** checkbox sits
right beside the device it uses.

## Audio Only on One Side

Press `Ctrl+P` to hear your current panning. Centre panning sends audio equally
to both channels; if one side is silent, the audio may simply be panned fully
to the other.

## Distorted or Clipping Audio

Turn the audio gain down: press `Ctrl+Shift+U`, arrow to Volume, then press
Down until it stops clipping.

If it is your transmitted audio that sounds distorted, open the Audio Workshop
with `Ctrl+Shift+W` and check that your microphone gain is not too high. The
Audio Workshop is for shaping transmitted audio and watching live meters — it
does not choose sound devices, so it is not where to go for a silent receiver.

## Remote (SmartLink) Audio Issues

- Audio over SmartLink is compressed with the Opus codec. A small amount of
  quality loss is normal and expected.
- Dropouts or choppy audio usually mean a network problem. A wired Ethernet
  connection often fixes what Wi-Fi jitter causes.
- If you hear nothing at all over SmartLink, work down this page from the top.
  The connected-state rung and the "through this computer" rung between them
  account for most of it.

## When You Need to Send Me the Evidence

If an audio problem will not yield to this page, capture it happening: press
`Ctrl+J`, then `Ctrl+D`, make the problem occur, and press the chord again to
stop. The capture saves as its own session you can export and send. The full
story is on the Diagnostic Log help page, and the controls live under Settings
on the Diagnostics tab.
