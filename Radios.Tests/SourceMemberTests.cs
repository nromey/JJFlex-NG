using System;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The shared member reader (#604) reads whole members, and fails loudly
    /// when it cannot see one.
    /// </summary>
    public sealed class SourceMemberTests
    {
        private const string CSharpSource =
            "class C\n" +
            "{\n" +
            "    private void First()\n" +
            "    {\n" +
            "        if (x) { Do(); }\n" +
            "        string s = \"a } brace in a string\";\n" +
            "        string v = @\"verbatim \"\" } brace\";\n" +
            "        string r = \"\"\"raw } \"quoted\" brace\"\"\";\n" +
            "        string i = $\"{x} and {y}\";\n" +
            "        char c = '}';\n" +
            "        char q = '\\'';\n" +
            "        // a } brace in a comment\n" +
            "        /* and } another */\n" +
            "        Last();\n" +
            "    }\n" +
            "\n" +
            "    private int Expression() => 4;\n" +
            "\n" +
            "    private void Second()\n" +
            "    {\n" +
            "        Other();\n" +
            "    }\n" +
            "}\n";

        [Fact]
        public void A_CSharp_member_is_read_whole_and_no_further()
        {
            string body = SourceMember.CSharp(CSharpSource, "private void First()");
            Assert.StartsWith("private void First()", body);
            Assert.EndsWith("Last();\n    }", body);
            Assert.DoesNotContain("Second", body);
            Assert.DoesNotContain("Expression", body);
        }

        [Fact]
        public void The_block_form_starts_at_the_opening_brace()
        {
            string block = SourceMember.CSharpBlock(CSharpSource, "private void Second()");
            Assert.Equal("{\n        Other();\n    }", block);
        }

        [Fact]
        public void A_member_longer_than_any_old_window_is_still_read_to_its_end()
        {
            string filler = new string('x', 20_000);
            string src = "void Long()\n{\n    // " + filler + "\n    TheLastLine();\n}\nvoid Next() { }\n";
            string body = SourceMember.CSharp(src, "void Long()");
            Assert.Contains("TheLastLine();", body);
            Assert.DoesNotContain("Next", body);
        }

        [Fact]
        public void A_missing_member_throws_rather_than_reading_nothing()
        {
            Assert.Throws<InvalidOperationException>(() => SourceMember.CSharp(CSharpSource, "private void Gone()"));
            Assert.Throws<InvalidOperationException>(() => SourceMember.CSharpBlock(CSharpSource, "private void Gone()"));
            Assert.Throws<InvalidOperationException>(() => SourceMember.VisualBasic(VbSource, "Private Sub Gone()"));
        }

        [Fact]
        public void An_expression_bodied_member_is_read_to_its_semicolon_not_into_the_next()
        {
            // The old private helpers read from the first '{' after the
            // signature, so an expression-bodied member came back with the
            // NEXT member's block attached.
            string body = SourceMember.CSharp(CSharpSource, "private int Expression()");
            Assert.Equal("private int Expression() => 4;", body);
            Assert.DoesNotContain("Second", body);
            Assert.Equal("=> 4;", SourceMember.CSharpBlock(CSharpSource, "private int Expression()"));

            string nested = "int M() => Call(x => { return 1; }, new[] { 2 });\nvoid Next() { }\n";
            Assert.Equal("int M() => Call(x => { return 1; }, new[] { 2 });", SourceMember.CSharp(nested, "int M()"));
        }

        [Fact]
        public void A_member_with_no_body_is_refused()
        {
            string src = "abstract void Declared();\nvoid Next() { Other(); }\n";
            Assert.Throws<InvalidOperationException>(() => SourceMember.CSharp(src, "abstract void Declared()"));
        }

        [Fact]
        public void The_search_can_start_past_an_earlier_match()
        {
            string src = "void M() { One(); }\nvoid M() { Two(); }\n";
            int second = src.IndexOf("void M()", 1, StringComparison.Ordinal);
            Assert.Contains("Two", SourceMember.CSharp(src, "void M()", second));
            Assert.Contains("One", SourceMember.CSharp(src, "void M()"));
        }

        private const string VbSource =
            "Module M\n" +
            "    Friend Sub Outer()\n" +
            "        Dim f = Sub()\n" +
            "                    Inner()\n" +
            "                End Sub\n" +
            "        If x Then\n" +
            "            Done()\n" +
            "        End If\n" +
            "    End Sub\n" +
            "\n" +
            "    Private Function Later() As Integer\n" +
            "        Return 1\n" +
            "    End Function\n" +
            "End Module\n";

        [Fact]
        public void A_VB_member_ends_at_its_own_End_line_not_a_lambdas()
        {
            string body = SourceMember.VisualBasic(VbSource, "Friend Sub Outer()");
            Assert.StartsWith("Friend Sub Outer()", body);
            Assert.EndsWith("    End Sub", body);
            Assert.Contains("Done()", body);
            Assert.DoesNotContain("Later", body);

            Assert.EndsWith("End Function", SourceMember.VisualBasic(VbSource, "Private Function Later() As Integer"));
        }

        [Fact]
        public void A_VB_member_with_Windows_line_endings_reads_the_same()
        {
            string crlf = VbSource.Replace("\n", "\r\n");
            string body = SourceMember.VisualBasic(crlf, "Friend Sub Outer()");
            Assert.Contains("Done()", body);
            Assert.EndsWith("End Sub", body);
            Assert.DoesNotContain("Later", body);
        }
    }
}
