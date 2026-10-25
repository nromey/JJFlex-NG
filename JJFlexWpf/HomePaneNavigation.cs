namespace JJFlexWpf;

/// <summary>The slice strip is index -1; visible field-group headers follow it.</summary>
internal static class HomePaneNavigation
{
    internal static int Next(int current, int groups, bool forward)
    {
        if (groups <= 0) return -1;
        return forward ? (current >= groups - 1 ? -1 : current + 1)
            : (current < 0 ? groups - 1 : current - 1);
    }
}
