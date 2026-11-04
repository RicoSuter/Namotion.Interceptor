using System.Text;

namespace Namotion.Devices.SunSpec.Generator;

/// <summary>
/// Writes indented C# with "\n" line endings.
/// </summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public void Raw(string text) => _builder.Append(text);

    public void Line(string text = "")
    {
        if (text.Length > 0)
        {
            _builder.Append(' ', _indent * 4).Append(text);
        }

        _builder.Append('\n');
    }

    public void Open()
    {
        Line("{");
        _indent++;
    }

    public void Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
    }

    public void Summary(string text)
    {
        Line("/// <summary>");
        Line($"/// {text}");
        Line("/// </summary>");
    }

    public override string ToString() => _builder.ToString();
}
