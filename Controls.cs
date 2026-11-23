namespace MonitorDial;

public enum ControlKind { Range, Choice, Toggle }

/// <summary>Descreve um ajuste do monitor (código VCP) e como apresentá-lo.</summary>
public sealed record VcpControl(
    string Key,
    string Label,
    string Section,
    byte Code,
    ControlKind Kind,
    int Min = 0,
    int Max = 100,          // 0 = usar o máximo informado pelo monitor
    int Step = 1,
    int WriteDivisor = 1,   // valor enviado = valor exibido / WriteDivisor
    (int Value, string Label)[]? Options = null,
    int OnValue = 1,
    int OffValue = 0,
    bool ReloadAllAfter = false,   // mudar este ajuste altera outros (ex.: modo de imagem)
    int SettleMs = 300,
    int[]? NeedsFreeSyncOff = null,   // opções que o monitor só aceita com FreeSync desligado
    string RejectHint = "")
{
    public int ToWire(int display) => display / WriteDivisor;
}

public static class ControlCatalog
{
    public const string SecImagem = "Imagem";
    public const string SecJogo = "Jogo";
    public const string SecCor = "Cor";
    public const string SecSom = "Som";

    /// <summary>
    /// Ajustes do LG 34WK500 confirmados por teste de escrita e leitura no monitor.
    /// </summary>
    public static readonly VcpControl[] All =
    {
        new("brightness", "Brilho", SecImagem, 0x10, ControlKind.Range),
        new("contrast", "Contraste", SecImagem, 0x12, ControlKind.Range),
        new("sharpness", "Nitidez", SecImagem, 0x87, ControlKind.Range),
        new("pictureMode", "Modo de imagem", SecImagem, 0x15, ControlKind.Choice, ReloadAllAfter: true, SettleMs: 1800,
            Options: new[]
            {
                (11, "Personalizado"), (1, "Leitura"), (32, "Foto"), (48, "Cinema"), (6, "Daltonismo"),
                (28, "Jogo FPS 1"), (29, "Jogo FPS 2"), (13, "Jogo RTS"), (14, "Personalizado (Jogo)"),
            }),
        new("aspect", "Proporção", SecImagem, 0xF5, ControlKind.Choice, SettleMs: 2000,
            NeedsFreeSyncOff: new[] { 3, 4, 0 },
            RejectHint: "Cinema 1, Cinema 2 e 1:1 só funcionam com o FreeSync desligado.",
            Options: new[] { (1, "Tela cheia"), (2, "Original"), (3, "Cinema 1"), (4, "Cinema 2"), (0, "1:1") }),
        new("energy", "Economia de energia", SecImagem, 0xF6, ControlKind.Choice, ReloadAllAfter: true, SettleMs: 1000,
            Options: new[] { (0, "Desligada"), (1, "Baixa"), (2, "Alta") }),

        // O monitor aceita escrita de 0 a 20 e devolve o valor multiplicado por 5.
        new("blackStabilizer", "Estabilizador de preto", SecJogo, 0xF9, ControlKind.Range, Step: 5, WriteDivisor: 5),
        new("responseTime", "Tempo de resposta", SecJogo, 0xF7, ControlKind.Choice,
            Options: new[] { (0, "Desligado"), (1, "Rápido"), (2, "Normal"), (3, "Lento") }),
        new("freeSync", "FreeSync", SecJogo, 0xF8, ControlKind.Toggle, OnValue: 1, OffValue: 0, ReloadAllAfter: true, SettleMs: 4000),

        new("colorTemp", "Temperatura de cor", SecCor, 0x14, ControlKind.Choice, ReloadAllAfter: true, SettleMs: 1500,
            Options: new[] { (5, "6500 K"), (8, "9300 K"), (11, "Personalizado") }),
        // Mexer em vermelho/verde/azul muda a temperatura de cor para "Personalizado".
        new("red", "Vermelho", SecCor, 0x16, ControlKind.Range),
        new("green", "Verde", SecCor, 0x18, ControlKind.Range),
        new("blue", "Azul", SecCor, 0x1A, ControlKind.Range),

        new("volume", "Volume", SecSom, 0x62, ControlKind.Range),
        new("mute", "Mudo", SecSom, 0x8D, ControlKind.Toggle, OnValue: 1, OffValue: 2,
            RejectHint: "Com o volume em 0 o monitor continua mudo."),
    };

    /// <summary>
    /// Ajustes que usam códigos próprios da LG. Em monitores de outras marcas esses
    /// códigos podem significar outra coisa, então não são exibidos.
    /// </summary>
    public static readonly HashSet<string> LgOnly = new()
    {
        "pictureMode", "aspect", "energy", "blackStabilizer", "responseTime", "freeSync",
    };

    public static IEnumerable<VcpControl> For(bool isLg) => All.Where(c => isLg || !LgOnly.Contains(c.Key));

    public static readonly string[] Sections = { SecImagem, SecJogo, SecCor, SecSom };

    public const byte ResetLuminance = 0x05;   // restaura brilho e contraste
    public const byte ResetColor = 0x08;       // restaura cor
}
