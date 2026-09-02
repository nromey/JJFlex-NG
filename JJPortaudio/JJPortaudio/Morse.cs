using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using JJTrace;
using JJPortaudio;

namespace JJPortaudio
{
    /// <summary>
    /// Send Morse code
    /// </summary>
    /// <remarks>
    /// public:
    ///   Morse - instanciate
    ///   start - Start audio
    ///   Stop - stop audio
    ///   Close - shutdown
    ///   Frequency - get/set frequency
    ///   Speed - get/set speed
    ///   Volume - get/set volume
    ///   Send - send a character or string.
    /// </remarks>
    public class Morse
    {
        private const string standardWord = "paris";
        // dit, or element, length in ms = ParisDividend/wpm.
        private const int ParisDividend = 1200;
        private float msPerElement
        {
            get { return ((float)ParisDividend / (float)_Speed); }
        }
        private float secondsPerElement
        {
            get { return msPerElement / 1000f; }
        }
        private float samplesPerElement
        {
            get { return (float)sampleRate * secondsPerElement; }
        }

        // spacing
        private const byte intraChar = 1;
        private const byte interChar = 3;
        private const byte interWord = 7;

        // character elements
        public const byte ignore = 0;
        public const byte space = 1;
        public const byte dit = 2;
        public const byte dah = 3;
        public const byte etx = 4;
        public const byte halfSpeed = 5;

        /// <summary>
        /// Morse code table
        /// </summary>
        /// <remarks>
        /// This jagged array contains one array per character.
        /// Each array specifies only the active elements, (i.e.) doesn't include intracharacter spaces.
        /// </remarks>
        public static byte[][] codeTable = new byte[128][]
        {
            // 0x00-0x0f - unused
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            // 0x10 unused
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            new byte[] { ignore },
            // 0x20-0x2f blank ! " # $ % & ' ( ) * + , - . /
            new byte[] { space}, // blank
            new byte[] { dah, dit, dah, dit, dah, dah }, // !
            new byte[] { dit, dah, dah, dit, dah, dit }, // "
            new byte[] { space }, // #
            //new byte[] { dit, dit, dit, dah, dit, dit, dah }, // $
            new byte[] { dit, dit, dit, dah, dit, dah }, // $ sk
            new byte[] { space }, // %
            new byte[] { dit, dah, dit, dit, dit }, // & as
            new byte[] { dit, dah, dah, dah, dah, dit }, // *
            new byte[] { dah, dit, dah, dah, dit }, // (
            new byte[] { dah, dit, dah, dah, dit, dah }, // )
            new byte[] { space }, // *
            new byte[] { dit, dah, dit, dah, dit }, // +
            new byte[] { dah, dah, dit, dit, dah, dah }, // ,
            new byte[] { dah, dit, dit, dit, dit, dah }, // -
            new byte[] { dit, dah, dit, dah, dit, dah }, // .
            new byte[] { dah, dit, dit, dah, dit }, // /
            // 0x30-0x3f 0-9 : ; < = > ?
            new byte[] { dah, dah, dah, dah, dah }, // 0
            new byte[] { dit, dah, dah, dah, dah }, // 1
            new byte[] { dit, dit, dah, dah, dah }, // 2
            new byte[] { dit, dit, dit, dah, dah }, // 3
            new byte[] { dit, dit, dit, dit, dah }, // 4
            new byte[] { dit, dit, dit, dit, dit }, // 5
            new byte[] { dah, dit, dit, dit, dit }, // 6
            new byte[] { dah, dah, dit, dit, dit }, // 7
            new byte[] { dah, dah, dah, dit, dit }, // 8
            new byte[] { dah, dah, dah, dah, dit }, // 9
            new byte[] { dah, dah, dah, dit, dit, dit }, // :
            new byte[] { dah, dit, dah, dit, dah, dit }, // ;
            new byte[] { space }, // <
            new byte[] { dah, dit, dit, dit, dah }, // = 
            new byte[] { space }, // >
            new byte[] { dit, dit, dah, dah, dit, dit }, // ?
            // 0x40-0x4f @ A-O
            new byte[] { dit, dah, dah, dit, dah, dit }, // @
            new byte[] { dit, dah }, // A
            new byte[] { dah, dit, dit, dit }, // B
            new byte[] { dah, dit, dah, dit }, // C
            new byte[] { dah, dit, dit }, // D
            new byte[] { dit }, // E
            new byte[] { dit, dit, dah, dit }, // F
            new byte[] { dah, dah, dit }, // G
            new byte[] { dit, dit, dit, dit }, // H
            new byte[] { dit, dit }, // I
            new byte[] { dit, dah, dah, dah }, // J
            new byte[] { dah, dit, dah }, // K
            new byte[] { dit, dah, dit, dit }, // L
            new byte[] { dah, dah }, // M
            new byte[] { dah, dit }, // N
            new byte[] { dah, dah, dah }, // O
            // 0x50-0x5f P-Z [ \ ] ^ _
            new byte[] { dit, dah, dah, dit }, // P
            new byte[] { dah, dah, dit, dah }, // Q
            new byte[] { dit, dah, dit }, // R
            new byte[] { dit, dit, dit }, // S
            new byte[] { dah }, // T
            new byte[] { dit, dit, dah }, // U
            new byte[] { dit, dit, dit, dah }, // V
            new byte[] { dit, dah, dah }, // W
            new byte[] { dah, dit, dit, dah }, // X
            new byte[] { dah, dit, dah, dah }, // Y
            new byte[] { dah, dah, dit, dit }, // Z
            new byte[] { dah, dit, dah, dah, dit }, // [
            new byte[] { space }, // \
            new byte[] { space }, // ]
            new byte[] { space }, // ^
            new byte[] { dit, dit, dah, dah, dit, dah }, // _
            // 0x60-0x6f ` a-o 
            new byte[] { space }, // `
            new byte[] { dit, dah }, // a
            new byte[] { dah, dit, dit, dit }, // b
            new byte[] { dah, dit, dah, dit }, // c
            new byte[] { dah, dit, dit }, // d
            new byte[] { dit }, // e
            new byte[] { dit, dit, dah, dit }, // f
            new byte[] { dah, dah, dit }, // g
            new byte[] { dit, dit, dit, dit }, // h
            new byte[] { dit, dit }, // i
            new byte[] { dit, dah, dah, dah }, // j
            new byte[] { dah, dit, dah }, // k
            new byte[] { dit, dah, dit, dit }, // l
            new byte[] { dah, dah }, // m
            new byte[] { dah, dit }, // n
            new byte[] { dah, dah, dah }, // o
            // 0x70-0x7f p-z { | } ~ [delta]
            new byte[] { dit, dah, dah, dit }, // p
            new byte[] { dah, dah, dit, dah }, // q
            new byte[] { dit, dah, dit }, // r
            new byte[] { dit, dit, dit }, // s
            new byte[] { dah }, // t
            new byte[] { dit, dit, dah }, // u
            new byte[] { dit, dit, dit, dah }, // v
            new byte[] { dit, dah, dah }, // w
            new byte[] { dah, dit, dit, dah }, // x
            new byte[] { dah, dit, dah, dah }, // y
            new byte[] { dah, dah, dit, dit }, // z
            new byte[] { space }, // {
            new byte[] { space }, // |
            new byte[] { space }, // }
            new byte[] { space }, // ~
            new byte[] { space }, // [delta]
        };

        /// <summary>
        /// True if started.
        /// </summary>
        public bool Started { get; set; }

        private const uint sampleRate = 24000;
        private const uint defaultFreq = 700;
        private uint _Frequency = defaultFreq;
        /// <summary>
        /// get/set frequency
        /// </summary>
        public uint Frequency
        {
            get { return _Frequency; }
            set { _Frequency = value; }
        }

        private const uint defaultSpeed = 20;
        private uint _Speed = defaultSpeed;
        /// <summary>
        /// get/set the speed
        /// </summary>
        public uint Speed
        {
            get { return _Speed; }
            set { _Speed = value; }
        }

        /// <summary>
        /// Effective speed
        /// </summary>
        public uint EffectiveSpeed = 0;

        private bool doingHalfSpeed = false;

        private float interCharSpaceCalc()
        {
            // it's interChar if EffectiveSpeed isn't used, or doing half speed.
            if (((EffectiveSpeed == 0) | (EffectiveSpeed >= Speed)) |
                doingHalfSpeed)
            {
                return (float)interChar;
            }

            int wordElements = 0;
            // Get elements in word w/o interword spaces.
            foreach (char c in standardWord)
            {
                byte[] b = codeTable[c];
                foreach (byte bb in b)
                {
                    wordElements += (bb == dah) ? 3 : 1;
                }
                wordElements += (b.Length - 1) * intraChar;
            }
            // time to send the word w/o inter values.
            float wordTime = (float)wordElements * msPerElement;
            // elements to send for the inter spaces.
            int interElements = ((standardWord.Length - 1) * interChar) + interWord;
            int totalElements = wordElements + interElements;
            float origWordTime = msPerElement * (float)totalElements;
            // time to send the new word.
            //float newWordTime = origWordTime * ((float)Speed / (float)EffectiveSpeed);
            float newWordTime = (ParisDividend / (float)EffectiveSpeed) * totalElements;
            // excess is new word time minus old word w/o inter values.
            float excess = newWordTime - wordTime;
            // get new, fractional, interChar elements.
            float excessElements = (float)interChar * ((excess / (float)interElements) / msPerElement);
            return excessElements;
        }

        private int _Volume;
        /// <summary>
        /// Volume, 0 through 100.
        /// </summary>
        public int Volume
        {
            get { return _Volume; }
            set
            {
                if (value < 0) value = 0;
                else if (value > 100) value = 100;
                _Volume = value;
            }
        }

        private float samplesPerCycle
        {
            get { return (float)sampleRate / (float)_Frequency; }
        }
        private const float omega = (2 * (float)Math.PI);
        private float sampleLength
        {
            get { return omega / samplesPerCycle; }
        }

        private JJAudioStream aud;
        private bool audOpen { get { return (aud != null); } }

        /// <summary>
        /// Morse sender
        /// </summary>
        /// <param name="sentCallback">(optional) Callback called when buffer empty.</param>
        public Morse(Audio.AudioSentCallback sentCallback = null)
        {
            Tracing.TraceLine("Morse", TraceLevel.Info);
            aud = new JJAudioStream();
            // Named, because this is the SECOND output stream a connected
            // session opens and its trace lines were indistinguishable from the
            // receive stream's (#473). It is fed only while somebody is keying,
            // so it spends most of a session filling the device with silence —
            // which is exactly what a starving receive stream also looks like.
            if (!aud.OpenAudio(Devices.DeviceTypes.output, sampleRate,
                    streamName: "CW monitor"))
            {
                Tracing.TraceLine("Morse:stream didn't open", TraceLevel.Error);
                aud = null;
            }
            aud.AudioSent = sentCallback;
        }

        /// <summary>
        /// Close
        /// </summary>
        public void Close()
        {
            Tracing.TraceLine("Morse.Close", TraceLevel.Info);
            aud.Close();
            aud = null;
        }

        /// <summary>
        /// Start the monitor
        /// </summary>
        /// <returns>true on success</returns>
        public bool Start()
        {
            Tracing.TraceLine("Morse.Start:" + Started.ToString(), TraceLevel.Info);
            if (!Started)
            {
                Started = (audOpen && aud.StartAudio());
            }
            return Started;
        }

        /// <summary>
        /// stop the monitor
        /// </summary>
        /// <returns>true</returns>
        public bool Stop()
        {
            bool rv = true;
            Tracing.TraceLine("Morse.Stop:" + Started.ToString(), TraceLevel.Info);
            if (Started)
            {
                Started = false;
                rv = (audOpen && aud.StopAudio());
            }
            return rv;
        }

        private const uint charElements = 4; // average elements in an alphabetic charactger.
        /// <summary>
        /// Send a character
        /// </summary>
        /// <param name="c">the character</param>
        /// <remarks>
        /// Each element is one dit long.
        /// sound indicates whether the oscillator is sounding at the start of an element.
        /// Recurse to send half speed char.
        /// </remarks>
        public void Send(char c)
        {
            Tracing.TraceLine("Morse.send:" + ((int)c).ToString(), TraceLevel.Info);
            if (c > codeTable.Length)
            {
                // Invalid input.
                return;
            }

            byte[] b = codeTable[c];
            if (b[0] == ignore) return;

            // process half speed char
            if (b[0] == halfSpeed)
            {
                doingHalfSpeed = true;
                uint origSpeed = Speed;
                Speed = (Speed + 1) / 2;
                Send((char)b[1]);
                Speed = origSpeed;
                doingHalfSpeed = false;
                return;
            }

            uint totalElements = 0;
            bool sound = false;
            uint elements = 0;
            // send initial element.
            sendElement(b[0], ref sound, ref elements);
            totalElements += elements;
            for (int i = 1; i < b.Length; i++)
            {
                sendElement(space, ref sound, ref elements); // intrachar
                totalElements += elements;
                sendElement(b[i], ref sound, ref elements);
                totalElements += elements;
            }
            writeBuf(sound, elements);

            // output intercharacter space, interChar space elements of length dit if not using effective speed.
            writeBuf(false, interCharSpaceCalc());
        }

        /// <summary>
        /// Send a morse string
        /// </summary>
        /// <param name="chars">the string</param>
        public void Send(string chars)
        {
            foreach (char c in chars)
            {
                Send(c);
            }
        }

        /// <summary>
        /// Seeif this is a Morse code character.
        /// </summary>
        /// <param name="c">the character</param>
        /// <returns>true if valid Morse character.</returns>
        public bool MorseCharacter(char c)
        {
            bool rv = false;
            if (c < codeTable.Length)
            {
                byte[] b = codeTable[c];
                rv = (b[0] == ignore) ? false : true;
            }
            return rv;
        }

        /// <summary>
        /// the audio sent callback.
        /// </summary>
        public Audio.AudioSentCallback SentCallback
        {
            get
            {
                return aud.AudioSent;
            }
            set
            {
                aud.AudioSent = value;
            }
        }

        private void sendElement(byte el,
            ref bool sound, ref uint elements)
        {
            if (sound)
            {
                // oscillator turning off if space.
                if (el == space)
                {
                    writeBuf(sound, elements);
                    sound = false;
                    elements = 0;
                }
            }
            else
            {
                // !sound, oscillator turning on if not space.
                if (el != space)
                {
                    if (elements > 0) writeBuf(sound, elements);
                    sound = true;
                    elements = 0;
                }
            }
            elements += (uint)((el == dah) ? 3 : 1);
        }

        private void writeBuf(bool sound, uint elements)
        {
            writeBuf(sound, (float)elements);
        }
        private void writeBuf(bool sound, float elements)
        {
            float samples = elements * samplesPerElement;
            //int intSamples = (int)(samples + 0.5);
            int intSamples = (int)(samples);
            float[] buf = new float[intSamples * 2]; // left and right channels
            float val = 0;
            float incr = sampleLength;
            for (int i = 0; i < buf.Length; i += 2)
            {
                if (sound)
                {
                    double volVal = Math.Pow((double)_Volume / 100, 2);
                    buf[i] = (float)(Math.Sin(val) * volVal);
                    //buf[i] = (float)Math.Sin(val) * ((float)_Volume / 100);
                    val += incr;
                }
                else buf[i] = 0f;
                buf[i + 1] = buf[i];
            }
            aud.Write(buf);
        }
    }
}
