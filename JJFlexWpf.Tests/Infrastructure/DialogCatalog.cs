using System.Collections;
using System.Reflection;
using System.Windows;
using Expression = System.Linq.Expressions.Expression;

namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>One dialog type the sweep knows about.</summary>
public sealed record DialogEntry(Type Type, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Why a dialog could not be brought up.</summary>
public sealed record ConstructionFailure(string Dialog, string Reason);

/// <summary>
/// Finds every window type in JJFlexWpf and builds one, in its disconnected
/// state, with no radio attached.
///
/// <para>The dialogs are discovered by reflection rather than listed by hand on
/// purpose: a hand-written list silently stops covering dialogs added after it
/// was written, and this suite exists because things go unnoticed.</para>
/// </summary>
public static class DialogCatalog
{
    /// <summary>
    /// Types deliberately left out of the sweep, each with the reason. Kept
    /// tiny and explicit - a skip that is not written down is a coverage lie.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DeclaredSkips = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AuthDialog"] =
            "Hosts a WebView2 control. Constructing it initialises the Edge runtime and starts a browser process; " +
            "the automation tree below the browser belongs to WebView2, not to us. Covered by Tier 2 or by hand.",
    };

    public static IReadOnlyList<DialogEntry> Discover()
    {
        var assembly = typeof(JJFlexDialog).Assembly;
        var result = new List<DialogEntry>();

        foreach (var type in SafeGetTypes(assembly))
        {
            if (!typeof(Window).IsAssignableFrom(type)) continue;
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
            if (type == typeof(JJFlexDialog)) continue;      // the base class itself carries no content
            if (DeclaredSkips.ContainsKey(type.Name)) continue;
            result.Add(new DialogEntry(type, type.Name));
        }

        return result.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
    }

    /// <summary>
    /// Builds the dialog. Must run on the UI thread. Returns null and fills
    /// <paramref name="failure"/> when the type cannot be brought up without a
    /// radio - that is a reported skip, never a silent one.
    /// </summary>
    public static Window? Construct(Type type, out ConstructionFailure? failure)
    {
        failure = null;
        var constructors = type
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => !c.IsPrivate)
            .OrderBy(c => c.GetParameters().Length)
            .ToList();

        if (constructors.Count == 0)
        {
            failure = new ConstructionFailure(type.Name, "No accessible constructor.");
            return null;
        }

        var reasons = new List<string>();
        foreach (var ctor in constructors)
        {
            try
            {
                var args = ctor.GetParameters().Select(p => Synthesize(p.ParameterType, p, 0)).ToArray();
                if (ctor.Invoke(args) is Window window) return window;
                reasons.Add("Constructor did not produce a Window.");
            }
            catch (Exception ex)
            {
                reasons.Add($"{Signature(ctor)} -> {TreeWalk.Describe(ex)}");
            }
        }

        failure = new ConstructionFailure(type.Name, string.Join(" | ", reasons));
        return null;
    }

    private static string Signature(ConstructorInfo ctor)
        => "(" + string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name)) + ")";

    /// <summary>
    /// Produces a stand-in value for a constructor parameter. Every dialog in
    /// this app takes either nothing, a plain configuration object, or a bag of
    /// delegates; none of them need a live radio to be laid out, so the rule is
    /// "give it something inert that will not throw when called".
    /// </summary>
    private static object? Synthesize(Type type, ParameterInfo? parameter, int depth)
    {
        if (parameter is { HasDefaultValue: true } && depth == 0 && parameter.DefaultValue != null)
            return parameter.DefaultValue;

        if (type == typeof(string)) return parameter?.Name ?? "Test";
        if (type == typeof(bool)) return false;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type.IsPrimitive) return Activator.CreateInstance(type);
        if (type == typeof(decimal)) return 0m;
        if (type == typeof(Guid)) return Guid.Empty;
        if (type == typeof(DateTime)) return DateTime.UnixEpoch;

        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return Synthesize(underlying, null, depth + 1);

        if (typeof(Delegate).IsAssignableFrom(type)) return StubDelegate(type);

        if (type.IsArray) return Array.CreateInstance(type.GetElementType()!, 0);

        if (type.IsInterface)
        {
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>) ||
                    definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>) ||
                    definition == typeof(IReadOnlyCollection<>))
                {
                    var listType = typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]);
                    return Activator.CreateInstance(listType);
                }
            }
            if (type == typeof(IEnumerable) || type == typeof(IList) || type == typeof(ICollection))
                return new List<object>();
            return null;   // an interface we do not recognise: the dialog gets null and we see what happens
        }

        if (type.IsAbstract) return null;

        if (depth >= 3) return null;

        // A plain object: build the cheapest constructor, recursively.
        var ctor = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor == null) return null;

        try
        {
            var args = ctor.GetParameters().Select(p => Synthesize(p.ParameterType, p, depth + 1)).ToArray();
            var instance = ctor.Invoke(args);
            FillDelegateProperties(instance, depth);
            return instance;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Callback bags in this app are plain classes of settable Func/Action
    /// properties, left null by default. Filling them with stubs is what stops
    /// a dialog from throwing a null reference while it populates itself, which
    /// would otherwise read as "cannot be constructed without a radio" when the
    /// truth is "was handed an empty bag".
    /// </summary>
    private static void FillDelegateProperties(object instance, int depth)
    {
        if (depth >= 3) return;
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            if (!typeof(Delegate).IsAssignableFrom(property.PropertyType)) continue;
            try
            {
                if (property.GetValue(instance) == null)
                    property.SetValue(instance, StubDelegate(property.PropertyType));
            }
            catch
            {
                // A read-only-in-practice property. Leave it.
            }
        }
    }

    /// <summary>
    /// A delegate of the requested shape that does nothing and returns the
    /// default. Built with an expression tree so it works for any Func or Action
    /// arity without a switch over twenty cases.
    /// </summary>
    public static Delegate? StubDelegate(Type delegateType)
    {
        try
        {
            var invoke = delegateType.GetMethod("Invoke");
            if (invoke == null) return null;
            if (invoke.GetParameters().Any(p => p.ParameterType.IsByRef)) return null;

            var parameters = invoke.GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();

            Expression body = invoke.ReturnType == typeof(void)
                ? Expression.Empty()
                : DefaultValueExpression(invoke.ReturnType);

            return Expression.Lambda(delegateType, body, parameters).Compile();
        }
        catch
        {
            return null;
        }
    }

    private static Expression DefaultValueExpression(Type type)
    {
        // A stub that returns an empty list is far more useful than one that
        // returns null: dialogs iterate what they are given.
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>) ||
                definition == typeof(List<>) || definition == typeof(IEnumerable<>) ||
                definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>))
            {
                var listType = typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]);
                return Expression.Convert(Expression.New(listType), type);
            }
        }
        if (type == typeof(string)) return Expression.Constant(string.Empty);
        return Expression.Default(type);
    }
}
