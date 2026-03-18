using System.Collections;
using System.Reflection;

namespace AuraLang.CodeGen;

internal static class AstReflection
{
    public static string? TryGetNameText(object node)
    {
        // Common shapes: node.Name.Text or node.Name (string)
        var nameProp = node.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public);
        if (nameProp is null) return null;

        var nameVal = nameProp.GetValue(node);
        if (nameVal is null) return null;

        if (nameVal is string s) return s;

        var textProp = nameVal.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
        if (textProp is not null)
        {
            var textVal = textProp.GetValue(nameVal) as string;
            if (!string.IsNullOrWhiteSpace(textVal)) return textVal;
        }

        return nameVal.ToString();
    }

    public static bool TryIsPublicVisibility(object node, out bool isPublic)
    {
        isPublic = false;
        var visProp = node.GetType().GetProperty("Visibility", BindingFlags.Instance | BindingFlags.Public);
        if (visProp is null) return false;

        var visVal = visProp.GetValue(node);
        if (visVal is null) return false;

        if (visVal is Enum en)
        {
            isPublic = string.Equals(en.ToString(), "Public", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        if (visVal is string s)
        {
            isPublic = string.Equals(s, "Public", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        return false;
    }

    public static IEnumerable? TryGetMembers(object node)
    {
        var memProp = node.GetType().GetProperty("Members", BindingFlags.Instance | BindingFlags.Public)
                    ?? node.GetType().GetProperty("Items", BindingFlags.Instance | BindingFlags.Public);
        if (memProp is null) return null;
        return memProp.GetValue(node) as IEnumerable;
    }

    public static IEnumerable? TryGetAttributes(object node)
    {
        var attrProp = node.GetType().GetProperty("Attributes", BindingFlags.Instance | BindingFlags.Public);
        if (attrProp is null) return null;
        return attrProp.GetValue(node) as IEnumerable;
    }

    public static object? TryGetPropertyValue(object node, params string[] names)
    {
        foreach (var n in names)
        {
            var p = node.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public);
            if (p is null) continue;
            return p.GetValue(node);
        }
        return null;
    }

    public static IEnumerable? TryGetEnumerableProperty(object node, params string[] names)
    {
        var v = TryGetPropertyValue(node, names);
        return v as IEnumerable;
    }

    public static object? TryGetTypeNode(object node, params string[] names)
    {
        return TryGetPropertyValue(node, names);
    }

    public static bool HasModifier(object node, string modifierName)
    {
        // Looks for node.Modifiers containing an enum/string matching modifierName
        var modsProp = node.GetType().GetProperty("Modifiers", BindingFlags.Instance | BindingFlags.Public);
        if (modsProp is null) return false;
        var modsVal = modsProp.GetValue(node);
        if (modsVal is null) return false;

        if (modsVal is IEnumerable en)
        {
            foreach (var m in en)
            {
                if (m is null) continue;
                if (m is string s)
                {
                    if (string.Equals(s, modifierName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (m is Enum ee)
                {
                    if (string.Equals(ee.ToString(), modifierName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }
}
