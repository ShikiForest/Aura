using System.Text;
using AuraLang.Ast;

public static class AstPrettyPrinter
{
    public static string Print(CompilationUnitNode cu)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CompilationUnit");
        for (int i = 0; i < cu.Items.Count; i++)
        {
            sb.AppendLine($"  [{i}] {cu.Items[i].GetType().Name}");
            sb.AppendLine("      " + cu.Items[i]);
        }
        return sb.ToString();
    }
}