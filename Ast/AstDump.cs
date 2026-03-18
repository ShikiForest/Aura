using System.Collections;
using System.Reflection;
using System.Text;
using AuraLang.Ast;

public static class AstDump
{
    public static string Dump(object? node, int maxDepth = 40)
    {
        var sb = new StringBuilder();
        DumpAny(sb, node, "", isLast: true, depth: 0, maxDepth);
        return sb.ToString();
    }

    private static void DumpAny(StringBuilder sb, object? obj, string indent, bool isLast, int depth, int maxDepth)
    {
        var branch = isLast ? "└─" : "├─";

        if (obj is null)
        {
            sb.AppendLine($"{indent}{branch}<null>");
            return;
        }

        if (depth >= maxDepth)
        {
            sb.AppendLine($"{indent}{branch}{obj.GetType().Name} …");
            return;
        }

        // primitive-ish
        if (obj is string s)
        {
            sb.AppendLine($"{indent}{branch}\"{s}\"");
            return;
        }
        if (obj.GetType().IsPrimitive || obj is decimal)
        {
            sb.AppendLine($"{indent}{branch}{obj}");
            return;
        }

        // IEnumerable (but not string)
        if (obj is IEnumerable en && obj is not SyntaxNode)
        {
            sb.AppendLine($"{indent}{branch}{obj.GetType().Name}");
            var nextIndent = indent + (isLast ? "  " : "│ ");
            var list = en.Cast<object?>().ToList();
            for (int i = 0; i < list.Count; i++)
            {
                DumpAny(sb, list[i], nextIndent, i == list.Count - 1, depth + 1, maxDepth);
            }
            return;
        }

        // SyntaxNode / records
        var type = obj.GetType();
        sb.AppendLine($"{indent}{branch}{type.Name}");

        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(p => p.GetIndexParameters().Length == 0)
                        .ToArray();

        var nextIndent2 = indent + (isLast ? "  " : "│ ");

        for (int i = 0; i < props.Length; i++)
        {
            var p = props[i];
            var value = p.GetValue(obj);
            var lastProp = i == props.Length - 1;

            // 先输出属性名
            sb.AppendLine($"{nextIndent2}{(lastProp ? "└─" : "├─")}{p.Name}:");

            // 再输出属性值（作为子节点）
            DumpAny(sb, value, nextIndent2 + (lastProp ? "  " : "│ "), true, depth + 1, maxDepth);
        }
    }
}