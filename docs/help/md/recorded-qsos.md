# Recorded QSOs

Record to file saves what your radio plays through this computer as a recording, filed by year and month. Recorded QSOs is where those recordings live: you can listen to one, step through it, trim it, save it as an MP3 or another kind of file, move it to the Recycle Bin, or clear out old ones.

Open it from the Audio menu, with **Ctrl+Alt+Shift+F11**, or with the JJ key and then **Alt+F** (F for files). It works with no radio connected, because the recordings are on this computer.

## The list

The list comes first, newest at the top. Each recording says when you made it and how long it is, for example "Today at 3:30 PM, 12 minutes 8 seconds". If you renamed a file, its new name comes first. A recording that is still being made says so, and you can't play, trim, save or delete it until you stop recording to file.

- **Up and Down** choose a recording.
- **Home and End** go to the newest and the oldest.
- **Enter** plays the one you are on through your speakers, from where you are in it. Press Enter again to stop.
- **Space** only selects. It never plays, and Ctrl+Space is still push to talk.
- **Left and Right** step back and forward through the recording by the step size. If it is playing, pause first.
- **Delete** moves it to the Recycle Bin, after asking.

If any file in the folder couldn't be read, I tell you how many when the window opens, rather than quietly showing you a shorter list.

## Which recording Play from computer plays

Play from computer (Ctrl+Alt+F11, or JJ key Ctrl+Shift+P) plays your newest recording, unless you choose another one here. Choosing a recording in the list makes it the one Play from computer plays, and unticks the checkbox called **Play from computer uses the newest recording**. Tick it again to go back to the newest. The choice lasts until you restart JJ Flexible.

## Listening and stepping

The step size is the next stop after the list: 1 second, 5 seconds, 10 seconds, 30 seconds or a minute. It starts at 5 seconds every time you open the window. After it come the Play button (it becomes Pause, then Resume), the Stop button while something is playing, and the playback position, which says where you are in words.

These keys work from anywhere in the window, even while a recording is playing:

- **P** pauses and resumes.
- **[** (left bracket) marks the start of a trim where you are.
- **]** (right bracket) marks the end.
- **Escape** stops the preview. Press Escape again to close the window. If you're transmitting, Escape unkeys first, as it does in every window.

Playing a recording here never keys the radio. While it plays, VOX (or CW break-in) is paused so your microphone can't key the radio on the sound from your speakers, and it comes back when the recording stops. Closing the window stops the preview too.

## Trimming

A trim is just two marks: where to start and where to end. Until you set one, the whole recording is selected. Play the recording, press **[** when you hear where you want it to start, and **]** where you want it to end. You can also pause, step with Left and Right, and use the Set trim start and Set trim end buttons. The line after them tells you what is selected, and Use whole recording clears both marks. Marks never change the recording itself; Save As writes the part you chose to a new file.

## Save As

Save As opens its own window: the file name, the folder and a Browse button, the file type, the part being saved, and then the options for that type.

- **FLAC** is lossless and the same format your recordings are made in. **WAV** is uncompressed. Both let you choose mono or stereo.
- **MP3** starts at 64 kilobits per second and **Opus** at 32. Both let you choose mono or stereo and the bitrate.
- **M4A** is HE-AAC, mono, at 32 kilobits per second, when Windows can make it.

The folder starts as the recording's own month folder every time. The file type and the end of the file name always match: choose a type and the name changes, or type a name ending in .mp3 and the type changes. Save As never touches the original, asks before replacing a file that's already there, and works in the background so you can keep using the window. If Windows can't make MP3 files on this computer, MP3 can't be chosen, and Ctrl+F1 on the file type says why and what to install.

## Delete and Prune

Delete and Prune move recordings to the Recycle Bin, so you can get them back. If the Recycle Bin can't take a file, for example on a network drive, I refuse and leave the file where it is. I never delete a recording outright.

Prune older recordings clears out old ones, and only when you ask. Choose either the oldest so many recordings, or the ones older than so many days, and type the number or step it with Up and Down. The window tells you how many would go and how much space they use, and lists them, before anything moves. Nothing is removed until you press the button that names the count and then say yes.
