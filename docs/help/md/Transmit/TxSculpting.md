---
keywords:
  - TX sculpting
  - equalizer
  - speech processor
  - compander
  - noise gate
  - transmit noise reduction
---

# TX Audio Sculpting

Use transmit (TX) audio sculpting to shape the sound of your transmitted voice. JJ Flexible Radio Access offers tools to reduce room noise before it reaches the radio, then shape your voice with the TX filter, transmit equalizer, speech processor and compander. All these tools help to sculpt different parts of the transmit audio path to support other operating techniques and tasks.

## Choosing Where to Start

Every voice is different, and band conditions can change during a contact. Sculpting your audio may help you reach rare DX, make more contest contacts, or simply be heard. Use the tools together to suit your voice and the conditions:

- Start with PC Cleanup if room noise is reaching the computer's microphone.
- Set TX filter edges to choose the range of frequencies in your signal. A wider signal takes up more spectrum but may sound fuller; a narrower signal fits into a crowded contest band.
- Use the transmit equalizer to raise or lower particular frequency ranges in your voice.
- Use the speech processor and compander to help voice transmissions carry through difficult or crowded band conditions.

## Cleaning Up the Room

PC Noise Cleanup reduces noise before the computer sends microphone audio to the radio. It acts only on the computer's microphone, never on a microphone plugged into the radio itself, and it does not work in CW and digital modes.

Press `Ctrl+Shift+W` to open the [Audio Workshop](AudioWorkshop.md), the hub for all things audio in JJ Flexible Radio Access. Then choose **This Computer** and find **PC Cleanup**. Its noise reduction and gate prepare the audio that the radio's own processing will shape.

### Transmit Noise Reduction

Turn on **Noise Reduction** to reduce room sounds such as fan noise, air conditioner sounds, or other quiet noise that is commonly heard in the shack. Adjust its strength to remove noise without removing your voice. Set **Listen to cleanup** to **What was removed**; if you hear your own words in the removed audio, lower the strength.

### Noise Gate

Turn on JJ Flexible Radio Access's **Noise Gate** when noise reduction may not be appropriate. Sometimes, noise reduction algorithms can introduce digital artifacts in transmission audio. If the chosen noise reduction algorithm introduces artifacts (your voice may sound watery or synthetic), try turning the noise reduction gain and strength down, or use the noise gate so that the noise reduction algorithms do not have to work as hard. A noise gate, if adjusted properly, only injects the voice into the transmission chain if the voice level exceeds the gate's level. In other words, the gate lowers the microphone level during pauses making silence truly silent. Its threshold follows the room's measured noise floor. The margin, attack, hold, release and depth controls adjust when it opens and how it fades down.

## Shaping Your Voice

The radio's TX filter, equalizer, speech processor and compander work on the audio the radio receives. Start with the filter edges and equalizer to shape your tone, then adjust the processing to suit the contact.

### Setting the TX Filter Edges

The low edge sets the lowest audio frequency passed by the TX filter; the high edge sets the highest. Widen the range for natural-sounding ragchew audio, or narrow it to improve readability in noisy conditions or during contests. Use the filter and equalizer together to suit your voice and avoid noise.

#### Adjusting TX Bandwidth from the Keyboard

Use these keys to adjust the TX filter edges without opening a dialog:

- Press `Ctrl+Shift+[` to lower the TX filter's low edge.
- Press `Ctrl+Shift+]` to raise the TX filter's low edge.
- Press `Ctrl+Alt+[` to lower the TX filter's high edge.
- Press `Ctrl+Alt+]` to raise the TX filter's high edge.

Each press moves the selected edge by one adaptive step. JJ Flexible will speak the new value. The step size depends on the current filter width:

- Below 200 hertz, each step is 10 hertz.
- From 200 to below 500 hertz, each step is 25 hertz.
- From 500 to below 3,500 hertz, each step is 50 hertz.
- From 3,500 to below 5,000 hertz, each step is 100 hertz.
- At 5,000 hertz or wider, each step is 200 hertz.

An ordinary SSB transmit filter uses steps of 50 hertz. The steps become finer as the filter narrows and coarser from 3.5 kilohertz onward. A narrow CW filter gets fine steps because of its width, rather than because CW is selected. Holding a key does not increase the rate of adjustment. Use the announced value to place the edge, because listening to the audio will not tell you when it has reached 2,800 hertz.

TX bandwidth will change if you change mode. CW and narrow digital signals do not need the wide audio range used for voice. If an audio frequency falls outside the adjusted transmit band, that part of the signal will not be heard.

The Radio menu's TX Filter submenu and the filter layer use the same step size. The edge moves by the same amount whichever method you use.

To set the edges in the Audio Workshop, press `Ctrl+Shift+W` and choose **Transmit Settings**:

- **TX Filter Low** sets the lowest frequency passed by the TX filter, typically 100 to 300 hertz.
- **TX Filter High** sets the highest frequency passed by the TX filter, typically 2,700 to 3,000 hertz.

#### Hearing filter values

Open the Radio menu, then **Transmit**, **TX Filter**, and **Read TX Filter**. The announcement gives the low and high edges. For example: "TX filter 100 to 2900."

Press `Ctrl+J`, then `F` to open the filter layer. Press `T` to select the TX filter, then `S` to hear its settings. This report includes the calculated bandwidth. With the same edges, it says: "TX filter 100 to 2900, 2.8 kilohertz."

The filter edges and calculated bandwidth are also displayed in the Audio Workshop's **Transmit Settings** category for reference.

#### Typical TX Filter Settings

- For **standard SSB**, use 100 to 2,800 hertz, giving a bandwidth of 2.7 kilohertz.
- For a **wide ragchew** setting, use 50 to 3,050 hertz, giving a bandwidth of 3.0 kilohertz.
- For a **narrow or contest** setting, use 200 to 2,600 hertz, giving a bandwidth of 2.4 kilohertz.
- For a **long-distance (DX) pileup**, use 300 to 2,700 hertz, giving a bandwidth of 2.4 kilohertz. Cutting the low end reduces rumble in crowded pileups.

### Shaping Your Voice with the Transmit Equalizer

The filter edges set the limits of your signal's audio range and actual signal width. The transmit equalizer raises or lowers particular frequency ranges to change the balance of your voice.
Open the Transmission section of [JJ Flexible Home](JJFlexibleHome.md) and press **Transmit Equalizer**. It has no key of its own yet.

- The **Equalizer** switch turns the equalizer on or off.
- The graphic equalizer has nine bands. Press the up or down arrow on a band, or type a level and press Enter to change the level of each equalizer band. JJ Flexible will speak the new level.
- Each change is sent to the radio automatically as you make it, so a station listening to your transmission will hear the changes you make to your sculpted signal immediately.
- If you make a change you cannot reverse, press the **Restore** button to return every band and the Equalizer switch to the settings that were present prior to entering the equalizer window.
- **Clear** sets every equalizer band to "flat".
- **Finished** keeps your changes. **Cancel** or Escape leaves the equalizer as you found it. Please note that restore performs the same action, but leaves the equalizer open so that it can be re-adjusted.

### Speech Processor

The speech processor raises average transmit power to help a voice survive noise and band congestion at the other end of a contact. In the Audio Workshop's **Transmit Settings** category, turn on **Speech Processor**, then choose **Processor Mode**: **Normal** for everyday contacts, **DX** for difficult conditions, or **DX+** for more punch with a more processed sound. Increase the processing only as far as conditions demand. These controls are also on the Radio menu's **Transmit** submenu.

### Compander

The compander evens out loud and soft syllables, raising average power without raising the peaks. Use it when quieter parts of your speech need to carry more evenly. In the Audio Workshop's **Transmit Settings** category, turn on **Compander** and adjust **Compander Level** from 0 to 100. Higher levels flatten the differences in your voice more strongly. The Radio menu's **Transmit** submenu also offers **Compander**, **Compander Level Up**, and **Compander Level Down**.

## Checking the Result

Ask a friend on the air for a signal report while you adjust your audio. What sounds good through your headphones may sound quite different on the air. An honest report from an on-air listener is the best way to judge the result.

When a friend is not available or conditions may not raise a contact to use as a radio check, open the Audio Workshop and press **Check your audio**. This window offers ways to hear what the radio will send over the antenna without a full-power transmission. Except for the microphone test, the radio does key: depending on the selected method, it uses low power, the transverter port, or a dummy load using specialized methods to allow the ham radio operator to hear how they sound. Ultimately, a radio check and using audio check in tandem can help to dial in your audio to the sweet spot when it comes to transmit audio sculpting.

