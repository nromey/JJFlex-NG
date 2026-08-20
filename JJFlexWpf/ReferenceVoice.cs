using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace JJFlexWpf;

/// <summary>
/// The reference voice recording that ships with the application, and the
/// operator's own reference if they have made one (Sprint 33 Track I).
/// </summary>
/// <remarks>
/// <para>
/// WHY A KNOWN FILE IS A PREREQUISITE, NOT A CONVENIENCE. Every transmit-audio
/// measurement taken up to now has been of somebody talking into a microphone,
/// and a person is a different signal every time — level, distance, timing and
/// words all move between runs. Two measurements taken that way cannot be
/// compared with each other, which means "did that change help?" has never had
/// an answer backed by evidence. The same audio, every run, forever, is what
/// turns a transmit measurement into a repeatable experiment.
/// </para>
/// <para>
/// TWO REFERENCES, BOTH REAL, AND THEY ANSWER DIFFERENT QUESTIONS. The shipped
/// file is the COMMON baseline: every operator has the identical bytes, so a
/// number one station reports means the same thing as the same number from
/// another, and a change in the application can be measured against something
/// that did not change. An operator's OWN recorded reference is the PERSONAL
/// baseline: their microphone, their room, their voice, which is the only
/// honest reference for their station and the only one that can answer "is my
/// audio better than it was last month". Neither replaces the other.
/// </para>
/// <para>
/// It ships as a plain file on disk rather than embedded in the assembly, for
/// three reasons: an operator can play it in whatever plays audio, to hear
/// what the reference actually sounds like before trusting measurements taken
/// with it; it is several megabytes, which is a lot to carry inside a DLL that
/// gets loaded whether or not anyone transmits; and the installer already
/// ships and the uninstaller already removes everything under the output
/// folder, so a file costs nothing to distribute correctly.
/// </para>
/// </remarks>
public static class ReferenceVoice
{
    /// <summary>The file name of the shipped reference recording.</summary>
    public const string FileName = "jjflex-reference-voice.wav";

    /// <summary>The file name of the script the shipped recording reads.</summary>
    public const string ScriptFileName = "jjflex-reference-voice.txt";

    /// <summary>
    /// The folder the shipped reference lives in, beside the application.
    /// </summary>
    public static string FolderPath
    {
        get
        {
            string baseDir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            return Path.Combine(baseDir, "Resources", "ReferenceVoice");
        }
    }

    /// <summary>Full path to the shipped reference recording.</summary>
    public static string FilePath => Path.Combine(FolderPath, FileName);

    /// <summary>
    /// Full path to the script the shipped recording reads, so an operator can
    /// read along, and so anyone rebuilding the recording says the same words.
    /// </summary>
    public static string ScriptPath => Path.Combine(FolderPath, ScriptFileName);

    /// <summary>True when the shipped reference is present.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return File.Exists(FilePath); }
            catch (Exception ex)
            {
                Trace.WriteLine($"ReferenceVoice.IsInstalled failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// The script text, or empty when it is not installed. Offered so a
    /// surface can show what is about to be transmitted — an operator should
    /// never have to transmit something to find out what it says.
    /// </summary>
    public static string ReadScript()
    {
        try
        {
            return File.Exists(ScriptPath) ? File.ReadAllText(ScriptPath) : "";
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"ReferenceVoice.ReadScript failed: {ex.Message}");
            return "";
        }
    }
}
