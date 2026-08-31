using System;
using System.Collections.Generic;
using System.Linq;

namespace RadioInTheLoop;

/// <summary>
/// The run's voice. Everything it prints is written to be READ ALOUD by a
/// screen reader following the console: short lines, the verdict as the first
/// word, no tables, no drawing, no decoration. The final line always begins
/// with the word RESULT, so "read last line" answers the only question that
/// matters.
/// </summary>
internal sealed class RunLog
{
    private sealed record CheckRecord(string Name, bool Pass, string Detail);
    private readonly List<CheckRecord> _checks = new();

    public int FailureCount => _checks.Count(c => !c.Pass);
    public int PassCount => _checks.Count(c => c.Pass);

    /// <summary>A phase heading, e.g. "Discovery".</summary>
    public void Phase(string text) => Console.WriteLine(Environment.NewLine + "== " + text);

    /// <summary>A plain progress sentence.</summary>
    public void Say(string text) => Console.WriteLine(text);

    /// <summary>Record and announce a passing check.</summary>
    public void Pass(string name, string detail)
    {
        _checks.Add(new CheckRecord(name, true, detail));
        Console.WriteLine("PASS " + name + ": " + detail);
    }

    /// <summary>Record and announce a failing check.</summary>
    public void Fail(string name, string detail)
    {
        _checks.Add(new CheckRecord(name, false, detail));
        Console.WriteLine("FAIL " + name + ": " + detail);
    }

    /// <summary>
    /// The closing summary. Repeats every failure right before the RESULT
    /// line, so nobody has to scroll back through a minute of progress lines
    /// to learn what went wrong.
    /// </summary>
    public void Summarize(string resultLine)
    {
        Console.WriteLine();
        if (FailureCount > 0)
        {
            Console.WriteLine("Failed checks, repeated:");
            foreach (var c in _checks.Where(c => !c.Pass))
                Console.WriteLine("  FAIL " + c.Name + ": " + c.Detail);
        }
        Console.WriteLine(resultLine);
    }
}
