#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Radios.Listenback;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// The automatic comparison stays inside this session, and a manual one that
/// reaches an older take says which session it came from (Noel, 2026-10-22
/// 07:10, round 14 bench). The sentences are DRAFT PROSE; the tests pin the
/// assembly and the session boundary.
/// </summary>
/// <remarks>
/// What these cannot see: that the dialog consults <see cref="TakeSession"/>
/// on the automatic road and not on the first take, which needs the bench.
/// </remarks>
public sealed class ListenbackTakeSessionTests
{
    [Fact]
    public void A_take_is_dated_by_its_directory_name_in_utc()
    {
        Assert.Equal(new DateTime(2026, 10, 22, 12, 6, 21, 258, DateTimeKind.Utc),
            TakeSession.MadeUtc(@"C:\x\Recordings\takes\20261022-120621-258-bc8dd8e6"));
    }

    [Fact]
    public void Last_nights_take_is_not_this_session_and_this_mornings_is()
    {
        var sessionStart = new DateTime(2026, 10, 22, 11, 55, 0, DateTimeKind.Utc);
        Assert.False(TakeSession.IsThisSession(@"C:\t\20261022-011551-424-a460e39d", sessionStart));
        Assert.True(TakeSession.IsThisSession(@"C:\t\20261022-120606-107-b5233d34", sessionStart));
    }

    [Fact]
    public void A_directory_named_some_other_way_falls_back_to_the_take_files_write_time()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjf-take-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string take = Path.Combine(dir, "take.txt");
            File.WriteAllText(take, "format=jjflex-iq-take-1");
            var written = new DateTime(2026, 10, 21, 1, 15, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(take, written);
            Assert.Equal(written, TakeSession.MadeUtc(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_earlier_session_sentence_names_today_yesterday_or_the_date()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var now = new DateTime(2026, 10, 22, 7, 20, 0);
            Assert.Equal("Your last take is from an earlier session, earlier today at 1:15 AM.",
                TakeSession.EarlierSessionNote(new DateTime(2026, 10, 22, 1, 15, 0), now));
            Assert.Equal("Your last take is from an earlier session, yesterday at 8:15 PM.",
                TakeSession.EarlierSessionNote(new DateTime(2026, 10, 21, 20, 15, 0), now));
            Assert.Equal("Your last take is from an earlier session, on Monday, October 19 at 9:05 AM.",
                TakeSession.EarlierSessionNote(new DateTime(2026, 10, 19, 9, 5, 0), now));
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }
}
