using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace JJFlexWpf
{
    /// <summary>
    /// The ONE loader for the native noise engines' libraries (#1016, Sprint 49
    /// Track S): load by absolute path, one handle per file per process, bind
    /// each export by name, validate before retaining, free on failure. Every
    /// RNNoise model, the v0.1.1 voice detector, DeepFilterNet3 and specbleach
    /// load through here, so the code that used to be copied into each engine
    /// (and that the integration pass caught duplicated) exists once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One handle per file, several bindings.</b> A library is loaded at most
    /// once per absolute path. Each caller names its binding (<c>api</c>), and a
    /// binding is made once per (path, name): the engine's old RNNoise model and
    /// the voice detector both bind <c>rnnoise_vad011.dll</c>, so they share its
    /// handle and each binds its own exports.
    /// </para>
    /// <para>
    /// <b>Validate, then retain; free on failure.</b> The caller's bind function
    /// binds and validates (a frame size, a version) and throws or returns null
    /// to refuse. A handle loaded for that call and refused is freed, so a
    /// rejected file can be deleted at once (RnnoiseBindingTests relies on it).
    /// A handle another binding already holds is never freed by a refusal.
    /// </para>
    /// <para>
    /// <b>Handles stay loaded for the process's lifetime</b> once a binding is
    /// retained, as every engine's own loader did before: a delegate bound from
    /// a handle must never outlive it.
    /// </para>
    /// <para>
    /// No <c>unsafe</c> code and no function pointers: delegates bound with
    /// <see cref="Marshal.GetDelegateForFunctionPointer{TDelegate}(IntPtr)"/> are
    /// the established shape, and neither project enables unsafe blocks.
    /// </para>
    /// </remarks>
    internal static class NativeEngineLibrary
    {
        private sealed class Library
        {
            internal Library(IntPtr handle) => Handle = handle;
            internal IntPtr Handle { get; }
            internal Dictionary<string, object> Bindings { get; } = new(StringComparer.Ordinal);
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Library> Libraries = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Bound = new(StringComparer.Ordinal);

        /// <summary>Every export name bound and retained in this process, for the tests (never a filename loader).</summary>
        internal static IReadOnlyCollection<string> BoundNames { get { lock (Gate) return Bound.ToArray(); } }

        /// <summary>Whether a library is loaded at this path, and how many bindings share its handle (tests).</summary>
        internal static int BindingCount(string path) { lock (Gate) return Libraries.TryGetValue(path, out var l) ? l.Bindings.Count : 0; }

        /// <summary>The handle loaded for a path, or zero (tests: two bindings share one).</summary>
        internal static IntPtr HandleOf(string path) { lock (Gate) return Libraries.TryGetValue(path, out var l) ? l.Handle : IntPtr.Zero; }

        /// <summary>A library's exports, bound by name for one binding.</summary>
        internal sealed class Exports
        {
            private readonly IntPtr _handle;
            private readonly List<string> _names = new();
            internal Exports(IntPtr handle) => _handle = handle;
            internal IReadOnlyList<string> Names => _names;

            /// <summary>Bind an export, throwing <see cref="EntryPointNotFoundException"/> when it is absent (as <see cref="NativeLibrary.GetExport"/> does).</summary>
            internal T Bind<T>(string name) where T : Delegate
            {
                var fn = Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_handle, name));
                _names.Add(name);
                return fn;
            }

            /// <summary>Bind an export, or null when it is absent.</summary>
            internal T? TryBind<T>(string name) where T : Delegate
            {
                if (!NativeLibrary.TryGetExport(_handle, name, out IntPtr fn)) return null;
                _names.Add(name);
                return Marshal.GetDelegateForFunctionPointer<T>(fn);
            }
        }

        /// <summary>
        /// Load the library at <paramref name="path"/> (throwing as
        /// <see cref="NativeLibrary.Load(string)"/> does when it will not load)
        /// and make the binding named <paramref name="api"/>, or return the one
        /// made before. <paramref name="bind"/> binds and validates; it throws to
        /// refuse.
        /// </summary>
        internal static TApi Load<TApi>(string path, string api, Func<Exports, TApi> bind) where TApi : class =>
            Make(path, api, bind, tryLoad: false, out _)!;

        /// <summary>
        /// As <see cref="Load"/>, without throwing for a file that will not load:
        /// null with <paramref name="loaded"/> false then. <paramref name="bind"/>
        /// returns null to refuse (then <paramref name="loaded"/> is true).
        /// </summary>
        internal static TApi? TryLoad<TApi>(string path, string api, Func<Exports, TApi?> bind, out bool loaded) where TApi : class =>
            Make(path, api, bind, tryLoad: true, out loaded);

        private static TApi? Make<TApi>(string path, string api, Func<Exports, TApi?> bind, bool tryLoad, out bool loaded) where TApi : class
        {
            lock (Gate)
            {
                Libraries.TryGetValue(path, out var library);
                if (library != null && library.Bindings.TryGetValue(api, out var existing))
                {
                    loaded = true;
                    return (TApi)existing;
                }
                bool fresh = library == null;
                if (fresh)
                {
                    IntPtr handle;
                    if (tryLoad)
                    {
                        if (!NativeLibrary.TryLoad(path, out handle)) { loaded = false; return null; }
                    }
                    else handle = NativeLibrary.Load(path);
                    library = new Library(handle);
                }
                loaded = true;
                TApi? made;
                var exports = new Exports(library!.Handle);
                try { made = bind(exports); }
                catch
                {
                    if (fresh) NativeLibrary.Free(library.Handle);
                    throw;
                }
                if (made == null)
                {
                    if (fresh) NativeLibrary.Free(library.Handle);
                    return null;
                }
                library.Bindings[api] = made;
                Libraries[path] = library;
                Bound.UnionWith(exports.Names);
                return made;
            }
        }
    }
}
