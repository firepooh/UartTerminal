using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Server;
using UartTerminal.Mcp;

namespace UartTerminal;

/// <summary>
/// MCP 도구 목록. 내용을 손으로 적지 않고 <see cref="UartMcpTools"/> 에서 <b>리플렉션으로 뽑는다</b>.
///
/// <para>이유가 둘이다. 하나는 문서로 옮겨 적으면 도구가 바뀔 때 조용히 어긋나기 때문이고,
/// 다른 하나는 이 화면에서 정작 알고 싶은 것이 <b>"AI 가 실제로 받는 것"</b> 이기 때문이다 —
/// 도구 설명과 인자 설명은 그대로 <c>tools/list</c> 로 나가는 문자열이고, 여기 보이는 것이 곧 그것이다.</para>
///
/// <para>그래서 이 화면의 본문은 번역하지 않는다(<c>Loc</c> 을 타지 않는다). AI 에게 가는 원문을
/// 그대로 보여주는 것이 목적이고, 번역하면 "무엇이 전달되는가" 라는 질문에 답하지 못한다.
/// 라벨·머리글처럼 <b>우리가 붙인 말</b>만 <c>Loc</c> 을 쓴다.</para>
/// </summary>
public partial class McpApiDialog : Window
{
    public McpApiDialog(Window? owner)
    {
        InitializeComponent();
        Owner = owner;

        var tools = typeof(UartMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(t => t.Attr is not null)
            // 읽기 계열을 먼저 — 읽기 전용 모드에서도 되는 것들이라 위험도가 낮다.
            .OrderByDescending(t => t.Attr!.ReadOnly == true)
            .ThenBy(t => t.Attr!.Name, StringComparer.Ordinal)
            .ToList();

        IntroText.Text = Loc.F("McpApi.Intro", tools.Count);
        foreach (var (_, attr) in tools) Summary.Children.Add(NameChip(attr!));
        foreach (var (method, attr) in tools) Body.Children.Add(Card(method, attr!));
    }

    /// <summary>훑기용 이름 칩. 색만으로 읽기/쓰기를 가른다 — 여기서는 이름 자체가 정보다.</summary>
    private UIElement NameChip(McpServerToolAttribute attr) => new TextBlock
    {
        Text = attr.Name,                                            // loc:data
        FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
        FontSize = (double)FindResource("Font.Caption"),
        Foreground = Theme.Brush(attr.ReadOnly == true ? "Green" : "Amber"),
        Margin = new Thickness(0, 2, 16, 2),
    };

    private UIElement Card(MethodInfo method, McpServerToolAttribute attr)
    {
        bool readOnly = attr.ReadOnly == true;

        var stack = new StackPanel();

        // 이름 + 성격 꼬리표
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock
        {
            Text = attr.Name,                                        // loc:data
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            FontSize = (double)FindResource("Font.Body"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(readOnly ? "Green" : "Amber"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new Border
        {
            Background = Theme.Brush("Surface"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Loc.S(readOnly ? "McpApi.Read" : "McpApi.Write"),
                FontSize = (double)FindResource("Font.Glyph"),
                Foreground = Theme.Brush("TextDim"),
            },
        });
        stack.Children.Add(head);

        // 설명 — tools/list 로 나가는 원문 그대로
        if (method.GetCustomAttribute<DescriptionAttribute>() is { } d)
            stack.Children.Add(new TextBlock
            {
                Text = d.Description,                                // loc:data
                FontSize = (double)FindResource("Font.Caption"),
                Foreground = Theme.Brush("TextDim"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

        // 인자 — CancellationToken 은 스키마에 안 나가므로 뺀다
        var args = method.GetParameters()
                         .Where(p => p.ParameterType != typeof(System.Threading.CancellationToken))
                         .ToList();
        stack.Children.Add(args.Count == 0
            ? new TextBlock
            {
                Text = Loc.S("McpApi.NoArgs"),
                FontSize = (double)FindResource("Font.Caption"),
                Foreground = Theme.Brush("TextFaint"),
                Margin = new Thickness(0, 8, 0, 0),
            }
            : ArgTable(args));

        return new Border
        {
            Style = (Style)FindResource("InsetPanel"),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    private UIElement ArgTable(System.Collections.Generic.List<ParameterInfo> args)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < args.Count; i++)
        {
            var p = args[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 이름 + (기본값 | 필수) — 무엇을 꼭 채워야 하는지가 한눈에 보여야 한다
            var name = new TextBlock
            {
                FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
                FontSize = (double)FindResource("Font.Caption"),
                Foreground = Theme.Brush("Text"),
                Margin = new Thickness(0, i == 0 ? 0 : 6, 14, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Text = p.Name,                                       // loc:data
            };
            Grid.SetRow(name, i);
            grid.Children.Add(name);

            var right = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            right.Children.Add(new TextBlock
            {
                Text = p.IsOptional
                    ? Loc.F("McpApi.Default", Describe(p.DefaultValue))
                    : Loc.S("McpApi.Required"),
                FontSize = (double)FindResource("Font.Glyph"),
                Foreground = Theme.Brush(p.IsOptional ? "TextFaint" : "Amber"),
            });
            if (p.GetCustomAttribute<DescriptionAttribute>() is { } pd)
                right.Children.Add(new TextBlock
                {
                    Text = pd.Description,                           // loc:data
                    FontSize = (double)FindResource("Font.Caption"),
                    Foreground = Theme.Brush("TextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });
            Grid.SetRow(right, i);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>기본값을 사람이 읽는 형태로. null 은 "생략" 이 더 정확하다 — 빈 값이 아니라 안 보내는 것이다.</summary>
    private static string Describe(object? value) => value switch
    {
        null => Loc.S("McpApi.Omitted"),
        bool b => b ? "true" : "false",                              // loc:data
        _ => value.ToString() ?? "",
    };
}
