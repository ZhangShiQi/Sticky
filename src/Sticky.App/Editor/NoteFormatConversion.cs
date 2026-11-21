using System.Globalization;
using System.IO;
using System.Text;
using Sticky.Core;
using Sticky.Sync.Git;

namespace Sticky.App.Editor;

internal sealed record NoteFormatConversion(Note Result, IReadOnlyList<string> Changes)
{
    public static NoteFormatConversion Preview(Note original)
    {
        var result = SyncJson.Read<Note>(SyncJson.Bytes(original));
        if (original.IsMarkdown)
        {
            var projection = result.ProjectMarkdown();
            result.SetRichText(projection.Blocks);
            return new(result, projection.ConversionNotes);
        }

        var changes = new List<string>();
        result.SetMarkdown(WriteBlocks(original.Blocks, changes, formatText: true));
        if (!SameContent(original, result))
        {
            result.SetMarkdown(WriteBlocks(original.Blocks, changes, formatText: false));
            changes.Add("部分强调样式与相邻文字的 Markdown 语法冲突，文字已按原文保留。");
        }
        if (!SameContent(original, result))
            throw new InvalidDataException("当前正文无法安全转换，原便签已保留。");
        return new(result, changes);
    }

    private static bool SameContent(Note original, Note converted)
        => original.GetImageIds().SequenceEqual(converted.GetImageIds()) && TextLines(original.PlainText)
            .SequenceEqual(TextLines(converted.PlainText));

    private static IEnumerable<string> TextLines(string text)
        => text.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length != 0);

    private static string WriteBlocks(IEnumerable<NoteBlock> blocks, List<string> changes, bool formatText)
    {
        var output = new List<string>();
        foreach (var block in blocks)
        {
            if (block.Alignment != "Left") AddChange(changes, "居中和右对齐会恢复为左对齐。");
            if (block.Kind == "list")
            {
                var items = new List<string>();
                for (var i = 0; i < block.Children.Count; i++)
                {
                    var prefix = block.Marker == "Decimal" ? (i + 1).ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                    var content = WriteBlocks(block.Children[i].Children, changes, formatText);
                    items.Add(prefix + content.Replace("\n", "\n" + new string(' ', prefix.Length)));
                }
                output.Add(string.Join("\n", items));
            }
            else output.Add(WriteInlines(block.Inlines, changes, formatText));
        }
        return string.Join("\n\n", output);
    }

    private static string WriteInlines(IEnumerable<NoteInline> inlines, List<string> changes, bool formatText)
    {
        var output = new StringBuilder();
        var text = new StringBuilder();
        (bool Bold, bool Italic, bool Strike) style = default;

        void Flush()
        {
            if (text.Length == 0) return;
            var lines = text.ToString().ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i != 0) output.Append("  \n");
                var line = lines[i].AsSpan();
                var content = line.Trim();
                if (!formatText || style == default || content.IsEmpty)
                {
                    Escape(output, line);
                    continue;
                }
                var start = line.Length - line.TrimStart().Length;
                Escape(output, line[..start]);
                if (style.Strike) output.Append("~~");
                var emphasis = (style.Bold ? "**" : "") + (style.Italic ? "*" : "");
                output.Append(emphasis);
                Escape(output, content);
                output.Append(emphasis);
                if (style.Strike) output.Append("~~");
                Escape(output, line[(start + content.Length)..]);
            }
            text.Clear();
        }

        foreach (var inline in inlines)
        {
            if (inline.Kind == "image" && inline.AssetId is { } id)
            {
                Flush();
                output.Append(MarkdownBody.ImageSyntax(id, inline.Width));
                continue;
            }
            if (inline.Underline) AddChange(changes, "下划线会移除。");
            if (inline.Foreground is not null) AddChange(changes, "自定义文字颜色会移除。");
            if (!(inline.UsesDefaultFontSize ?? inline.FontSize == 16)) AddChange(changes, "自定义字号会移除。");
            var nextStyle = formatText ? (inline.Bold, inline.Italic, inline.Strike) : default;
            if (style != nextStyle) Flush();
            style = nextStyle;
            text.Append(inline.Text);
        }
        Flush();
        return output.ToString();
    }

    private static void Escape(StringBuilder output, ReadOnlySpan<char> text)
    {
        const string syntax = @"\`*_{}[]<>()#+-.!&|=~";
        foreach (var ch in text)
        {
            if (ch == '\t') output.Append("&#9;");
            else if (ch == ' ' && (output.Length == 0 || output[^1] == '\n')) output.Append("&#32;");
            else
            {
                if (syntax.Contains(ch)) output.Append('\\');
                output.Append(ch);
            }
        }
    }

    private static void AddChange(List<string> changes, string change)
    {
        if (!changes.Contains(change)) changes.Add(change);
    }
}
