#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// The words of "start a new test or add to this one" (Q7, D5), asked before
/// the countdown when the microphone stand-ins, the mic profile or the radio
/// differ from the test's last take. ALL DRAFT PROSE, awaiting Noel.
/// </summary>
public static class TakeAdmissionText
{
    /// <summary>The window's title.</summary>
    public static string Title => Lexicon.Get("audio.takes.admission_title");

    /// <summary>"Since take 3 of W6R-23T, your mic profile and the computer's microphone have changed."</summary>
    public static string Message(TakeTest test, IReadOnlyList<string> changed)
    {
        int last = test.Reservations.Where(r => r.State == TakeReservation.Made).Select(r => r.Number).DefaultIfEmpty(0).Max();
        var words = changed.Select(What).Where(w => w.Length > 0).ToList();
        string list = words.Count <= 1 ? words.FirstOrDefault() ?? ""
            : string.Join(", ", words.Take(words.Count - 1)) + " " + Lexicon.Get("audio.takes.and") + " " + words[^1];
        string key = words.Count == 1 ? "audio.takes.admission_message_one" : "audio.takes.admission_message_many";
        return Lexicon.Get(key, ("number", last), ("test", Name(test)), ("changes", list));
    }

    /// <summary>"Start a new test, or add this take to W6R-23T?"</summary>
    public static string Question(TakeTest test) => Lexicon.Get("audio.takes.admission_question", ("test", Name(test)));

    public static string NewTestLabel => Lexicon.Get("audio.takes.admission_new");
    public static string AddLabel => Lexicon.Get("audio.takes.admission_add");

    /// <summary>Said when the question was backed out of with Escape: nothing started.</summary>
    public static string BackedOut => Lexicon.Get("audio.takes.admission_backed_out");

    private static string Name(TakeTest test) => test.Name.Length > 0 ? test.Name : test.ReadableId;

    /// <summary>The changed fact, named as what it is, never as "the microphone" (D5).</summary>
    private static string What(string changed)
    {
        // The key is computed first: the lexicon scanner reads a literal key.
        string key = changed switch
        {
            "mic-input" => "audio.takes.admission_what.mic_input",
            "pc-microphone-device" => "audio.takes.admission_what.pc_microphone",
            "mic-profile" => "audio.takes.admission_what.mic_profile",
            "serial" => "audio.takes.admission_what.radio",
            _ => "",
        };
        return key.Length == 0 ? "" : Lexicon.Get(key);
    }
}
