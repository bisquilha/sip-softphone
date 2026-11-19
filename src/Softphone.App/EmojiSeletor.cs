using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Softphone.App;

/// <summary>1.2.11: grade de emojis (reações e caixa de mensagem). Categorias, recentes e uma linha de favoritos; o mesmo painel serve aos dois usos.</summary>
internal sealed class EmojiSeletor : Border
{
    internal static readonly string[] Rapidos = { "👍", "❤️", "😂", "😮", "😢", "🙏" };

    // categorias (nome da aba, ícone, emojis separados por espaço)
    private static readonly (string Nome, string Aba, string Lista)[] Categorias =
    {
        ("Rostos", "😀", "😀 😃 😄 😁 😆 😅 🤣 😂 🙂 🙃 😉 😊 😇 🥰 😍 🤩 😘 😗 😚 😙 😋 😛 😜 🤪 😝 🤑 🤗 🤭 🤫 🤔 🤐 🤨 😐 😑 😶 😏 😒 🙄 😬 🤥 😌 😔 😪 🤤 😴 😷 🤒 🤕 🤢 🤮 🤧 🥵 🥶 🥴 😵 🤯 🤠 🥳 😎 🤓 🧐 😕 😟 🙁 ☹️ 😮 😯 😲 😳 🥺 😦 😧 😨 😰 😥 😢 😭 😱 😖 😣 😞 😓 😩 😫 🥱 😤 😡 😠 🤬 😈 👿 💀 ☠️ 💩 🤡 👻 👽 🤖 😺 😸 😹 😻 😼 😽 🙀 😿 😾 🙈 🙉 🙊"),
        ("Gestos", "👍", "👍 👎 👌 ✌️ 🤞 🤟 🤘 🤙 👈 👉 👆 👇 ☝️ ✋ 🤚 🖐️ 🖖 👋 🤝 👏 🙌 👐 🤲 🙏 ✍️ 💪 🦾 🦵 🦶 👂 👃 🧠 👀 👁️ 👅 👄 🙋 🙆 🙅 🤷 🤦 💁 🙇 🤳 💅"),
        ("Corações", "❤️", "❤️ 🧡 💛 💚 💙 💜 🖤 🤍 🤎 💔 ❣️ 💕 💞 💓 💗 💖 💘 💝 💟 💯 💢 💥 💫 💦 💨 🕳️ 💬 💭 💤 ✨ ⭐ 🌟 ⚡ 🔥 🎉 🎊 🎈 🎁 🏆 🥇 🥈 🥉 🏅"),
        ("Natureza", "🐶", "🐶 🐱 🐭 🐹 🐰 🦊 🐻 🐼 🐨 🐯 🦁 🐮 🐷 🐸 🐵 🐔 🐧 🐦 🐤 🦆 🦅 🦉 🦇 🐺 🐗 🐴 🦄 🐝 🐛 🦋 🐌 🐞 🐜 🕷️ 🐢 🐍 🦎 🐙 🦑 🦀 🐠 🐟 🐬 🐳 🐋 🦈 🐊 🐘 🦒 🦓 🦍 🐕 🐈 🌵 🎄 🌲 🌳 🌴 🌱 🌿 ☘️ 🍀 🍁 🍂 🍃 🌺 🌻 🌹 🌷 🌼 🌸 💐 🌞 🌝 🌙 ⭐ ☀️ ⛅ ☁️ 🌧️ ⛈️ ❄️ ☃️ 🌈 🌊"),
        ("Comida", "🍕", "🍏 🍎 🍐 🍊 🍋 🍌 🍉 🍇 🍓 🍈 🍒 🍑 🥭 🍍 🥥 🥝 🍅 🥑 🥦 🥒 🌽 🥕 🥔 🍞 🥐 🥖 🧀 🥚 🍳 🥓 🍔 🍟 🌭 🍕 🥪 🌮 🌯 🥗 🍝 🍜 🍲 🍛 🍣 🍱 🍤 🍙 🍚 🍦 🍰 🎂 🍫 🍬 🍭 🍩 🍪 ☕ 🍵 🥤 🍺 🍻 🥂 🍷 🥃 🍸 🍹"),
        ("Atividades", "⚽", "⚽ 🏀 🏈 ⚾ 🎾 🏐 🏉 🎱 🏓 🏸 🥊 🥋 🎯 ⛳ 🎣 🎮 🕹️ 🎲 🧩 ♟️ 🎨 🎭 🎬 🎤 🎧 🎼 🎹 🥁 🎷 🎺 🎸 🎻 🚗 🚕 🚌 🚓 🚑 🚒 🚚 🚜 🏍️ 🚲 ✈️ 🚀 🚁 🚂 ⛵ 🚢 🏠 🏢 🏭 🏥 🏦 🏨 ⛪ 🌍 🌎 🌏 🗺️ ⏰ ⌛ 📱 💻 🖥️ ⌨️ 🖨️ 📷 📞 ☎️ 🔋 💡 🔦 📚 📖 📝 ✏️ 📌 📎 📁 📂 📅 📊 📈 📉 💰 💳 🔑 🔒 🔓 🔧 🔨 ⚙️ 🧰 📦 🚪 🛒"),
        ("Símbolos", "✅", "✅ ❌ ⭕ ❗ ❓ ❕ ❔ ‼️ ⁉️ ⚠️ 🚫 ⛔ ✔️ ➕ ➖ ➗ ✖️ ♻️ 🔁 🔄 ⬆️ ⬇️ ⬅️ ➡️ ↗️ ↘️ ↙️ ↖️ ▶️ ⏸️ ⏹️ ⏺️ ⏭️ ⏮️ 🔔 🔕 🔊 🔇 📢 📣 💲 ™️ ©️ ®️ 🆗 🆕 🆒 🆓 🔝 🔜 🅰️ 🅱️ 🆎 🆘 ℹ️ 🔴 🟠 🟡 🟢 🔵 🟣 ⚫ ⚪ 🟥 🟧 🟨 🟩 🟦 🟪 ⬛ ⬜ 🔶 🔷 🏁 🚩 🎌"),
    };

    public event Action<string>? Escolhido;

    private readonly WrapPanel _grade = new() { Margin = new Thickness(4) };
    private readonly TextBlock _titulo = new() { FontSize = 11, Margin = new Thickness(8, 0, 0, 2) };
    private readonly StackPanel _abas = new() { Orientation = Orientation.Horizontal };
    private readonly List<string> _recentes;

    public EmojiSeletor(List<string>? recentes, bool comRapidos)
    {
        _recentes = recentes ?? new List<string>();
        Width = 330;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(4);
        SetResourceReference(BackgroundProperty, "BgSurface");
        SetResourceReference(BorderBrushProperty, "BorderStrong");
        _titulo.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var raiz = new StackPanel();
        if (comRapidos)
        {
            var linha = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
            foreach (var e in Rapidos) linha.Children.Add(Botao(e, 26, 40));
            raiz.Children.Add(linha);
            raiz.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 2) });
        }
        raiz.Children.Add(_abas);
        raiz.Children.Add(_titulo);
        raiz.Children.Add(new ScrollViewer { Height = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _grade, Focusable = false });
        Child = raiz;

        if (_recentes.Count > 0) AdicionarAba("Recentes", "🕘", () => _recentes.ToArray());
        foreach (var (nome, aba, lista) in Categorias)
        {
            var itens = lista.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            AdicionarAba(nome, aba, () => itens);
        }
        Mostrar(_recentes.Count > 0 ? "Recentes" : "Rostos");
    }

    private readonly Dictionary<string, (Button Aba, Func<string[]> Itens)> _mapa = new();

    private void AdicionarAba(string nome, string simbolo, Func<string[]> itens)
    {
        var b = new Button { Content = new Emoji.Wpf.TextBlock { Text = simbolo, FontSize = 16 }, ToolTip = nome, Width = 34, Height = 30, FontSize = 16, Margin = new Thickness(1), Padding = new Thickness(0), Cursor = Cursors.Hand, BorderThickness = new Thickness(0) };
        b.SetResourceReference(Control.BackgroundProperty, "BgSurface");
        b.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        b.Template = ModeloPlano();
        b.Click += (_, _) => Mostrar(nome);
        _abas.Children.Add(b);
        _mapa[nome] = (b, itens);
    }

    private void Mostrar(string nome)
    {
        if (!_mapa.TryGetValue(nome, out var x)) return;
        foreach (var (n, v) in _mapa) v.Aba.SetResourceReference(Control.BackgroundProperty, n == nome ? "AccentSoft" : "BgSurface");
        _titulo.Text = nome;
        _grade.Children.Clear();
        foreach (var e in x.Itens()) _grade.Children.Add(Botao(e, 22, 34));
    }

    private Button Botao(string emoji, double fonte, double lado)
    {
        var b = new Button { Content = new Emoji.Wpf.TextBlock { Text = emoji, FontSize = fonte, HorizontalAlignment = HorizontalAlignment.Center }, FontSize = fonte, Width = lado, Height = lado, Margin = new Thickness(1), Padding = new Thickness(0), Cursor = Cursors.Hand, BorderThickness = new Thickness(0), Template = ModeloPlano() };
        b.Background = Brushes.Transparent;
        b.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        b.Click += (_, _) => Escolhido?.Invoke(emoji);
        return b;
    }

    /// <summary>Botão sem o visual padrão do Windows, com destaque ao passar o mouse.</summary>
    private static ControlTemplate ModeloPlano()
    {
        var t = new ControlTemplate(typeof(Button));
        var borda = new FrameworkElementFactory(typeof(Border));
        borda.Name = "fundo";
        borda.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        borda.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var conteudo = new FrameworkElementFactory(typeof(ContentPresenter));
        conteudo.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        conteudo.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        borda.AppendChild(conteudo);
        t.VisualTree = borda;
        var gatilho = new Trigger { Property = IsMouseOverProperty, Value = true };
        gatilho.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), "fundo"));
        t.Triggers.Add(gatilho);
        return t;
    }

    /// <summary>Guarda o emoji no começo da lista de recentes (máx. 24).</summary>
    public static void LembrarRecente(List<string> recentes, string emoji)
    {
        recentes.Remove(emoji);
        recentes.Insert(0, emoji);
        if (recentes.Count > 24) recentes.RemoveRange(24, recentes.Count - 24);
    }
}
