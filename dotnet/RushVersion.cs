using System.Reflection;

static class RushVersion
{
    public static string Full { get; } =
        typeof(Rush.ScriptEngine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0-dev";
}
