/*
 * Tolk - .NET wrapper for the Tolk screen reader abstraction library
 *
 * Copyright (c) 2014-2024 Davy Kager
 * Licensed under the GNU Lesser General Public License (LGPL) version 2.1
 *
 * Original source: https://github.com/dkager/tolk
 *
 * Tolk provides a unified interface to multiple screen readers:
 * JAWS, NVDA, Window-Eyes, SuperNova, System Access, ZoomText, and SAPI.
 */

using System;
using System.Runtime.InteropServices;

namespace Radios
{
    /// <summary>
    /// Tolk screen reader abstraction library wrapper.
    /// Provides a unified interface to speak through NVDA, JAWS, and other screen readers.
    /// </summary>
    public sealed class Tolk
    {
        private const string DLL_NAME = "Tolk.dll";

        #region Native P/Invoke Declarations

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Load();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_IsLoaded();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_Unload();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.Bool)] bool trySAPI);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void Tolk_PreferSAPI([MarshalAs(UnmanagedType.Bool)] bool preferSAPI);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr Tolk_DetectScreenReader();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_HasSpeech();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_HasBraille();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Output([MarshalAs(UnmanagedType.LPWStr)] string str, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Speak([MarshalAs(UnmanagedType.LPWStr)] string str, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Braille([MarshalAs(UnmanagedType.LPWStr)] string str);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_IsSpeaking();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Tolk_Silence();

        #endregion

        // Private constructor - all methods are static
        private Tolk() { }

        #region Public API

        /// <summary>
        /// Initializes Tolk by loading and initializing the screen reader drivers.
        /// Must be called before using any other Tolk functions.
        /// </summary>
        public static void Load()
        {
            Tolk_Load();
        }

        /// <summary>
        /// Returns whether Tolk has been initialized.
        /// </summary>
        public static bool IsLoaded()
        {
            return Tolk_IsLoaded();
        }

        /// <summary>
        /// Unloads Tolk and releases screen reader drivers.
        /// Should be called before application exit.
        /// </summary>
        public static void Unload()
        {
            Tolk_Unload();
        }

        /// <summary>
        /// Sets whether SAPI should be used as a fallback when no screen reader is active.
        /// Must be called before Load() to take effect.
        /// </summary>
        /// <param name="trySAPI">True to enable SAPI fallback, false to disable.</param>
        public static void TrySAPI(bool trySAPI)
        {
            Tolk_TrySAPI(trySAPI);
        }

        /// <summary>
        /// Sets whether SAPI should be preferred over screen readers.
        /// Must be called before Load() to take effect.
        /// </summary>
        /// <param name="preferSAPI">True to prefer SAPI, false to prefer screen readers.</param>
        public static void PreferSAPI(bool preferSAPI)
        {
            Tolk_PreferSAPI(preferSAPI);
        }

        /// <summary>
        /// Returns the name of the currently active screen reader, or null if none detected.
        /// </summary>
        public static string DetectScreenReader()
        {
            IntPtr ptr = Tolk_DetectScreenReader();
            if (ptr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringUni(ptr);
        }

        /// <summary>
        /// Returns whether the current screen reader driver supports speech output.
        /// </summary>
        public static bool HasSpeech()
        {
            return Tolk_HasSpeech();
        }

        /// <summary>
        /// Returns whether the current screen reader driver supports braille output.
        /// </summary>
        public static bool HasBraille()
        {
            return Tolk_HasBraille();
        }

        /// <summary>
        /// Outputs text through both speech and braille if available.
        /// </summary>
        /// <param name="str">The text to output.</param>
        /// <param name="interrupt">If true, interrupts any current speech.</param>
        /// <returns>True if output was successful.</returns>
        public static bool Output(string str, bool interrupt = false)
        {
            return Tolk_Output(str, interrupt);
        }

        /// <summary>
        /// Speaks text through the screen reader.
        /// </summary>
        /// <param name="str">The text to speak.</param>
        /// <param name="interrupt">If true, interrupts any current speech.</param>
        /// <returns>True if speech was successful.</returns>
        public static bool Speak(string str, bool interrupt = false)
        {
            return Tolk_Speak(str, interrupt);
        }

        /// <summary>
        /// Outputs text to the braille display if available.
        /// </summary>
        /// <param name="str">The text to display.</param>
        /// <returns>True if braille output was successful.</returns>
        public static bool Braille(string str)
        {
            return Tolk_Braille(str);
        }

        /// <summary>
        /// Returns whether Tolk is currently speaking.
        /// Note: Not all screen readers support this.
        /// </summary>
        public static bool IsSpeaking()
        {
            return Tolk_IsSpeaking();
        }

        /// <summary>
        /// Silences any current speech.
        /// </summary>
        /// <returns>True if silence was successful.</returns>
        public static bool Silence()
        {
            return Tolk_Silence();
        }

        #endregion
    }
}
