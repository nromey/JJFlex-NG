# Audio Troubleshooting

Silence is the hardest thing to diagnose by ear, because the evidence is
missing. This page walks the causes in the order they actually bite, starting
with the one that catches almost everybody the first time.

If you would rather have JJ Flex check for you, open Settings, go to the Audio
tab, and press the **Why is my radio silent?** button. It walks the same ladder
against your radio right now and speaks the first thing it finds wrong.

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
radio connects** choice, right under the checkbox, has three settings:
remember how I left it (the default), always on for this radio, or always
off. Always on is the remote operator's insurance — even if a session ends
with PC audio off, the next connect turns it back on. Whatever happens at
connect is announced out loud, so the switch is never flipped silently.

If the checkbox is off on a remote connection, turn it on. JJ Flex will say
what it did.

## Then: Are the Right Sound Devices Chosen?

Radio audio through your computer needs two devices: one to play the radio's
receive audio, and one microphone to send back to the radio. Choose them in
Settings, Audio tab, **Audio Devices** button — or from the Audio menu, where
the same dialog is called Audio Devices.

That dialog covers everything in one place: the radio's playback device, the
microphone sent to the radio, the device your alerts and CW notifications play
through, and the meter tone device. The current choice is announced when the
dialog opens, and the system default is marked in words.

A few things worth knowing:

- **If a device you chose is unplugged**, JJ Flex falls back to your system
  default and says so out loud. It does not go quiet, and it does not silently
  bind to whatever device happened to take the missing one's place. Your
  original choice stays saved, so plugging the device back in picks it up
  again.
- **Moving a USB headset to a different port is fine.** JJ Flex identifies your
  saved devices by name, not by position in the list, so a reshuffle rebinds
  silently and correctly.
- **Only stereo devices are listed.** A mono microphone will not appear. That
  is a JJ Flex limitation, not a fault with your microphone.
- **The device list is a snapshot.** If you plug something in while the dialog
  is open, press the **Refresh device list** button.

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
