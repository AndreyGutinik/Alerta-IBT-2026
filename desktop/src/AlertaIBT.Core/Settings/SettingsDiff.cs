using System.Collections;
using System.Reflection;

namespace AlertaIBT.Core.Settings;

/// <summary>Names the settings that differ between two snapshots (names only, never values: some are secrets).</summary>
public static class SettingsDiff
{
    private const int MaxNames = 6;

    /// <summary>The changed keys as <c>Section.Key</c>, at most six, or <c>(nada)</c>.</summary>
    public static string Keys(AppSettings before, AppSettings after)
    {
        var names = new List<string>();
        try
        {
            foreach (var section in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var a = section.GetValue(before);
                var b = section.GetValue(after);
                if (Same(a, b))
                {
                    continue;
                }

                var added = false;
                if (a is not null && b is not null && !IsSimple(section.PropertyType))
                {
                    foreach (var key in section.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (key.GetIndexParameters().Length == 0 && !Same(key.GetValue(a), key.GetValue(b)))
                        {
                            names.Add(section.Name + "." + key.Name);
                            added = true;
                        }
                    }
                }

                if (!added)
                {
                    names.Add(section.Name);
                }
            }
        }
        catch (Exception)
        {
            return "(nao foi possivel comparar)";
        }

        if (names.Count == 0)
        {
            return "(nada)";
        }

        return names.Count <= MaxNames ? string.Join(", ", names) : string.Join(", ", names.Take(MaxNames)) + $" e mais {names.Count - MaxNames}";
    }

    private static bool IsSimple(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);

    private static bool Same(object? a, object? b)
    {
        if (Equals(a, b))
        {
            return true;
        }

        if (a is IEnumerable left && b is IEnumerable right && a is not string)
        {
            var l = left.Cast<object?>().ToList();
            var r = right.Cast<object?>().ToList();
            return l.Count == r.Count && l.Zip(r).All(pair => Same(pair.First, pair.Second));
        }

        return false;
    }
}
