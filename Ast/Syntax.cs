using Antlr4.Runtime;

namespace AuraLang.Ast;

/// <summary>源位置（Index 以字符索引为准；Line/Column 来自 ANTLR token，Line 从 1 开始）。</summary>
public readonly record struct SourcePos(int Index, int Line, int Column);

/// <summary>源区间：End 是“结束后一个位置”（类似 [Start, End)）。</summary>
public readonly record struct SourceSpan(SourcePos Start, SourcePos End)
{
    public static SourceSpan Merge(SourceSpan a, SourceSpan b)
        => new(
            a.Start.Index <= b.Start.Index ? a.Start : b.Start,
            a.End.Index >= b.End.Index ? a.End : b.End
        );
}

public static class SpanFactory
{
    public static SourceSpan From(ParserRuleContext ctx)
    {
        var start = ctx.Start;
        var stop = ctx.Stop ?? ctx.Start;
        return From(start, stop);
    }

    public static SourceSpan From(IToken token) => From(token, token);

    public static SourceSpan From(IToken start, IToken stop)
    {
        // Token 的 StopIndex 是包含式；我们把 EndIndex 设为 StopIndex+1
        var startIndex = start.StartIndex;
        var endIndex = stop.StopIndex >= 0 ? stop.StopIndex + 1 : startIndex;

        // Column 只是行内起始列；EndColumn 用 stop.Column + tokenTextLength 粗略估计（足够做诊断定位）
        var startPos = new SourcePos(startIndex, start.Line, start.Column);
        var stopTextLen = stop.Text?.Length ?? 0;
        var endPos = new SourcePos(endIndex, stop.Line, stop.Column + stopTextLen);

        return new SourceSpan(startPos, endPos);
    }
}