---
keywords:
  - recorded QSOs
  - recordings, computer
  - trim a recording
  - save a recording as MP3
  - prune recordings
---

# Recorded QSOs

Record to file saves what your radio plays through this computer as a recording, filed by year and month. Recorded QSOs is where those recordings live: you can listen to one, step through it, trim it, save it as an MP3 or another kind of file, move it to the Recycle Bin, or clear out old ones.

Open it from the Audio menu, with **Ctrl+Alt+Shift+F11**, or with the JJ key and then **Alt+F** (F for files). It works with no radio connected, because the recordings are on this computer.

## The list

The list comes first, newest at the top. Each recording says when you made it and how long it is, for example "Today at 3:30 PM, original QSO recording, 12 minutes 8 seconds". If you renamed a file, its new name comes first. A recording that is still being made says so, and you can't play, trim, save or delete it until you stop recording to file. The list keeps up while it is open: start or stop a recording and it appears, or gets its length, without closing the window.

Files you make with Save As are in the list too, so Delete and Prune can reach them. Those say when you saved them and as what, for example "Today at 11:02 AM, saved as Opus, under a second"; one you gave a name of your own leads with that name. You can play them here, Opus included; trimming and Save As work on the recorder's own FLAC files.

The selected file's full name, with its extension, is in the read-only File name box just before the list: press Shift+Tab from the list to read it, a character at a time if you like, or to copy it.

- **Press Up and Down** to select a recording.
- **Home and End** go to the newest and the oldest.
- **Enter** starts or pauses playback of the selected recording, from the play head.
- With VOX on and **Sends key the radio when VOX is on** checked, **Enter** sends the selected recording over the air instead, the way Play from computer does, and Enter again pauses or carries on the send. See "Which recording Play from computer plays" below.
- **S** stops and returns the play head to the beginning of the file.
- **Space** only selects. It never plays, and Ctrl+Space is still push to talk.
- **Left and Right** move the play head backward and forward by the selected step size, including during playback, when you are on the list or the position slider. **Ctrl+Left and Ctrl+Right** do it from anywhere in the window.
- **Delete** moves the selected recording to the Recycle Bin.

If any file in the folder couldn't be read, I tell you how many when the window opens, rather than quietly showing you a shorter list.

## Which recording Play from computer plays

Play from computer (Ctrl+Alt+F11, or JJ key Ctrl+Shift+P) plays your newest recording, unless you select another one here: then it plays the selected recording. Making a new recording puts it back on the newest, and so does restarting JJ Flexible. The window always opens on the recording Play from computer will play, so the one you see selected is the one that goes.

Press Play from computer while you are transmitting and the same recording goes out over the air, and you hear it through the radio's transmit monitor. Your microphone comes back when it ends, and you keep the key. With VOX on and **Sends key the radio when VOX is on** checked, Play from computer keys the radio itself; see Push to Talk and Transmission.

Press Play from computer again while the recording is playing, on your speakers or over the air, and it pauses, holding its place; press it once more to carry on. Escape in the main window stops it. A recording longer than three minutes asks before it is sent, and after ten minutes on the air a send pauses by itself so you can identify. Long recordings go out whole, ten minutes at a time; see Push to Talk and Transmission.

While a recording plays back on your computer, the radio's audio on your computer drops by 12 dB, so you hear the recording over the band rather than mixed into it. To change that, go to Settings and use **Radio audio during playback**: Unchanged, Lowered 6, 12, 18 or 24 dB, or Muted. It works on the audio you hear through the computer only; the radio's own speaker is untouched.

## Listening and stepping

The step size is the next stop after the list: 0.1 seconds, 0.5 seconds, 1 second, 5 seconds, 10 seconds, 30 seconds or a minute. It designates the size of the step used to jump forward and backward through a recorded file, and JJ Flexible remembers the one you chose, even after a restart. After it come the Play button (it becomes Pause, then Resume), the Stop button while something is playing, and the playback position, which says where the play head is in words, to the tenth of a second.

These keys work from anywhere in the window, even while a recording is playing:

- **S** stops and returns the play head to the beginning of the file.
- **[** (left bracket) sets the trim start mark at the play head.
- **]** (right bracket) sets the trim end mark at the play head.
- **Shift+[** and **Shift+]** clear those marks, and **Backspace** clears the selection.
- **Escape**, **S** and the Stop button stop a recording being sent over the air first, if one is going out or paused; after that, Escape closes the window and S returns the play head to the beginning.
- **Escape** stops playback while a recording is playing, as S does; press it again to close the window. While playback is paused, Escape closes the window straight away. If you're transmitting, Escape unkeys first, as it does in every window.

Playing a recording here never keys the radio, unless VOX is on and Sends key the radio when VOX is on is checked: then Enter sends it, as above. While a preview plays, VOX (or CW break-in) is paused so your microphone can't key the radio on the sound from your speakers, and it comes back when the recording stops. Closing the window stops the preview too.

## Trimming

A trim is just two marks: where to start and where to end. Until you set one, the whole recording is selected. Play the recording, press **[** when you hear where you want it to start, and **]** where you want it to end. You can also pause with Enter, move the play head with Left and Right (a step of 0.1 seconds is good for finding the exact spot), and use the Set trim start and Set trim end buttons. The line after them tells you what is selected. Shift+[ or Shift+] clears one mark, and Backspace or the Clear selection button clears both and says "Trim marks cleared." Marks never change the recording itself; Save As writes the part you chose to a new file.

## Save As

Save As opens its own window: the file name, the folder and a Browse button, the file type, the part being saved, and then the options for that type.

- **FLAC** is lossless and the same format your recordings are made in. **WAV** is uncompressed. Both let you choose mono or stereo.
- **MP3** starts at 64 kilobits per second and **Opus** at 32. Both let you choose mono or stereo and the bitrate.
- **M4A** is HE-AAC, mono, at 32 kilobits per second, when Windows can make it.

When Save As writes into the recording's own folder, it tells you the month, for example "Saved QSO-2026-10-25-102646-trim.opus in the October 2026 folder". If you chose another folder, it says the whole path.

The folder starts as the recording's own month folder every time. The file type and the end of the file name always match: choose a type and the name changes, or type a name ending in .mp3 and the type changes. Save As never touches the original, asks before replacing a file that's already there, and works in the background so you can keep using the window. If Windows can't make MP3 files on this computer, MP3 can't be chosen, and Ctrl+F1 on the file type says why and what to install.

## Delete and Prune

Delete and Prune move recordings to the Recycle Bin, so you can get them back. If the Recycle Bin can't take a file, for example on a network drive, I refuse and leave the file where it is. I never delete a recording outright.

Prune older recordings clears out old ones, and only when you ask. Choose the oldest so many recordings, the ones older than so many days, or keep the newest so many and move the rest (the three choices are next to each other, so Up and Down move between them), then Tab to the number and type it or step it with Up and Down. The window tells you how many would go and how much space they use, and lists them, before anything moves. Nothing is removed until you press the button that names the count and then say yes.
