using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Neo.Shared;

namespace NeoSetup;

/// <summary>
/// Fenêtre de l'installateur (ou du désinstallateur, selon le mode) : une étape à la fois —
/// choix, progression, terminé. Construite en code, aux couleurs de la suite Neo.
/// </summary>
public sealed class SetupForm : Form
{
    private static readonly Color Background = Color.FromArgb(20, 20, 24);
    private static readonly Color Card = Color.FromArgb(32, 32, 38);
    private static readonly Color Accent = Color.FromArgb(30, 155, 255);
    private static readonly Color Danger = Color.FromArgb(232, 72, 85);
    private static readonly Color Secondary = Color.FromArgb(170, 175, 185);

    private readonly InstallerPackage? _payload;
    private readonly bool _uninstall;
    private bool _updating;

    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly Panel _body = new();
    private readonly Button _primary = new();
    private readonly Button _secondary = new();

    private bool _working;
    private bool _finished;
    private CheckBox? _launchAfter;

    public SetupForm(InstallerPackage? payload, bool uninstall)
    {
        _payload = payload;
        _uninstall = uninstall;

        // Mise à jour : NeoSuite est déjà là, le titre de la fenêtre le dit dès l'ouverture
        Text = uninstall ? "Désinstaller NeoSuite" : Installer.IsInstalled ? "Mettre à jour NeoSuite" : "Installer NeoSuite";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(640, 540);
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        BuildLayout();
        if (uninstall)
            ShowUninstallChoices();
        else
            ShowInstallChoices();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Barre de titre sombre (Windows 10 2004+ et 11)
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    private void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(32, 28, 32, 0) };
        var logo = new PictureBox { Size = new Size(64, 64), Location = new Point(32, 28), SizeMode = PictureBoxSizeMode.Zoom };
        using (var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("Logo.png"))
        {
            if (stream is not null)
                logo.Image = Image.FromStream(stream);
        }
        _title.SetBounds(112, 30, 480, 36);
        _title.Font = new Font("Segoe UI Semibold", 18f);
        _subtitle.SetBounds(114, 68, 480, 24);
        _subtitle.ForeColor = Secondary;
        header.Controls.AddRange([logo, _title, _subtitle]);

        // Boutons alignés à droite : action principale tout à droite, comme dans Windows
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(22, 16, 22, 0),
        };
        StyleButton(_primary, Accent, Color.White);
        StyleButton(_secondary, Card, Color.White);
        footer.Controls.AddRange([_primary, _secondary]);

        _body.Dock = DockStyle.Fill;
        _body.Padding = new Padding(32, 8, 32, 0);

        Controls.Add(_body);
        Controls.Add(footer);
        Controls.Add(header);
    }

    // ======================================================================
    // Installation
    // ======================================================================

    private void ShowInstallChoices()
    {
        var package = _payload;
        var info = package?.ReadInfo();
        var installed = Installer.InstalledVersion;
        var target = info?.SuiteVersion;

        // Quatre cas : première installation, mise à jour, même version, ou version plus ancienne que celle en place
        var mode = installed is null ? Mode.Install
            : target is null || target > installed ? Mode.Update
            : target == installed ? Mode.Reinstall
            : Mode.Older;
        _updating = mode != Mode.Install;

        _title.Text = mode == Mode.Update ? "Mettre à jour NeoSuite" : "NeoSuite";
        _subtitle.Text = mode switch
        {
            Mode.Update => $"Version {Short(installed)}  →  {Short(target)}",
            Mode.Reinstall => $"La version {Short(installed)} est déjà installée",
            Mode.Older => $"La version {Short(installed)}, plus récente, est déjà installée",
            _ => "Vos applications Neo, réunies dans un seul panneau",
        };

        var apps = package?.Apps() ?? [];
        var intro = Paragraph(mode switch
        {
            Mode.Update => "Une nouvelle version de NeoSuite est prête à être installée. Vos applications, vos réglages et vos raccourcis sont conservés.",
            Mode.Reinstall => "Cette version est déjà à jour. Vous pouvez la réinstaller pour réparer une installation abîmée.",
            Mode.Older => "Cet installateur contient une version plus ancienne que celle de ce PC. L'installer ferait revenir NeoSuite en arrière.",
            _ => "NeoSuite s'installe dans un panneau à droite de l'écran (Ctrl + Alt + N). Depuis ce panneau, vous installez ensuite les applications dont vous avez besoin :",
        });
        var list = new Label
        {
            AutoSize = false,
            Height = 48,
            Dock = DockStyle.Top,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10f),
            Text = apps.Count == 0 ? "" : string.Join("  ·  ", apps.Select(a => a.Name)),
            Padding = new Padding(0, 4, 0, 0),
        };

        // Les nouveautés de la version qu'on installe (une première installation n'en a pas besoin)
        Control[] news = mode is Mode.Update && info is { Notes.Count: > 0 }
            ? [Paragraph("Nouveautés", Color.White), Paragraph(string.Join("\r\n", info.Notes.Take(6).Select(n => "•  " + n)), Secondary)]
            : [];

        // Une mise à jour garde les choix déjà faits
        var startup = Check("Lancer NeoSuite au démarrage de Windows", installed is null || Installer.StartupEnabled);
        var desktop = Check("Créer un raccourci sur le Bureau", installed is null || Installer.DesktopShortcutExists);
        var location = Paragraph($"Emplacement : {Installer.Root}", Secondary);

        AddToBody([intro, .. (mode == Mode.Install ? new Control[] { list } : []), .. news, startup, desktop, location]);

        _primary.Text = mode switch
        {
            Mode.Update => "Mettre à jour",
            Mode.Reinstall => "Réinstaller",
            Mode.Older => "Installer quand même",
            _ => "Installer",
        };
        _secondary.Text = "Annuler";
        _primary.Enabled = package is not null;
        SetHandler(_primary, async () => await RunAsync(progress =>
            Installer.Install(package!, new InstallOptions(startup.Checked, desktop.Checked), progress), ShowInstallDone));
        SetHandler(_secondary, Close);
        AcceptButton = _primary;

        if (package is null)
            AddToBody(Paragraph($"Les fichiers à installer sont introuvables : « {Path.GetFileName(InstallerPackage.DataPathFor(Environment.ProcessPath!))} » doit rester dans le même dossier que ce programme.", Danger));
    }

    private enum Mode
    {
        Install,
        Update,
        Reinstall,
        Older,
    }

    private static string Short(Version? v) => v is null ? "?" : v.Revision > 0 ? v.ToString() : v.ToString(3);

    private void ShowInstallDone()
    {
        _title.Text = _updating ? "NeoSuite est à jour" : "C'est prêt !";
        _subtitle.Text = _updating ? "La mise à jour est terminée" : "NeoSuite est installé";
        _launchAfter = Check("Ouvrir NeoSuite maintenant", true);
        AddToBody(
            Paragraph(_updating
                ? "Les applications Neo qui ont une nouvelle version affichent « Mettre à jour » dans le panneau (Ctrl + Alt + N)."
                : "Ouvrez le panneau avec Ctrl + Alt + N, ou en cliquant sur l'icône NeoSuite près de l'horloge."),
            Paragraph(_updating
                ? "NeoSoft, s'il est installé, fait de même pour les logiciels du catalogue."
                : "Cliquez ensuite sur une application pour l'installer : NeoDriver, NeoUninstaller, NeoBoost, NeoSoft… NeoSoft vous propose à son tour NeoRar, NeoMedia et des dizaines de logiciels courants.", Secondary),
            _launchAfter);
        _primary.Text = "Terminer";
        _secondary.Visible = false;
        SetHandler(_primary, () =>
        {
            if (_launchAfter.Checked)
                Installer.LaunchSuite();
            Close();
        });
    }
    // ======================================================================
    // Désinstallation
    // ======================================================================

    private void ShowUninstallChoices()
    {
        _title.Text = "Désinstaller NeoSuite";
        _subtitle.Text = "Vous pourrez le réinstaller à tout moment";

        var removeApps = Check("Désinstaller aussi les applications Neo installées (NeoDriver, NeoRar, NeoMedia…)", true);
        var removeSettings = Check("Supprimer aussi mes paramètres et historiques", false);
        AddToBody(Paragraph("NeoSuite et le catalogue des applications seront supprimés de ce PC."), removeApps, removeSettings);

        _primary.Text = "Désinstaller";
        _primary.BackColor = Danger;
        _secondary.Text = "Annuler";
        SetHandler(_primary, async () => await RunAsync(progress =>
            Installer.Uninstall(new UninstallOptions(removeApps.Checked, removeSettings.Checked), progress), ShowUninstallDone));
        SetHandler(_secondary, Close);
    }

    private void ShowUninstallDone()
    {
        _title.Text = "NeoSuite est désinstallé";
        _subtitle.Text = "Merci de l'avoir utilisé";
        AddToBody(Paragraph("Tout a été retiré de ce PC."));
        _primary.Text = "Fermer";
        _primary.BackColor = Accent;
        _secondary.Visible = false;
        SetHandler(_primary, Close);
        Installer.ScheduleSelfDelete();
    }

    // ======================================================================
    // Progression
    // ======================================================================

    private async Task RunAsync(Action<IProgress<(string Text, double Percent)>> work, Action onDone)
    {
        _working = true;
        var status = Paragraph("Préparation…");
        var bar = new ProgressBar { Dock = DockStyle.Top, Height = 8, Maximum = 1000, Style = ProgressBarStyle.Continuous };
        AddToBody(status, Spacer(12), bar);
        _primary.Enabled = false;
        _secondary.Enabled = false;

        var progress = new Progress<(string Text, double Percent)>(p =>
        {
            status.Text = p.Text;
            bar.Value = (int)Math.Clamp(p.Percent * 10, 0, 1000);
        });

        try
        {
            await Task.Run(() => work(progress));
            _finished = true;
            onDone();
        }
        catch (Exception ex)
        {
            AddToBody(Paragraph("Une erreur est survenue :", Danger), Paragraph(ex.Message));
            _primary.Text = "Fermer";
            _secondary.Visible = false;
            SetHandler(_primary, Close);
        }
        finally
        {
            _working = false;
            _primary.Enabled = true;
            _secondary.Enabled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Pas d'interruption en pleine copie : l'installation resterait à moitié faite
        if (_working && !_finished)
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    // ======================================================================
    // Petits éléments d'interface
    // ======================================================================

    private void AddToBody(params Control[] controls)
    {
        _body.SuspendLayout();
        _body.Controls.Clear();
        // Dock Top : le dernier ajouté s'affiche en haut, d'où l'ordre inversé
        foreach (var control in controls.Reverse())
            _body.Controls.Add(control);
        _body.ResumeLayout();
    }

    private static Label Paragraph(string text, Color? color = null) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        AutoSize = false,
        Height = MeasureHeight(text),
        ForeColor = color ?? Color.White,
        Padding = new Padding(0, 4, 0, 4),
    };

    private static int MeasureHeight(string text)
    {
        using var font = new Font("Segoe UI", 10f);
        var size = TextRenderer.MeasureText(text, font, new Size(570, 0), TextFormatFlags.WordBreak);
        return size.Height + 12;
    }

    private static CheckBox Check(string text, bool isChecked) => new()
    {
        Text = text,
        Checked = isChecked,
        Dock = DockStyle.Top,
        Height = 34,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Standard,
    };

    private static Panel Spacer(int height) => new() { Dock = DockStyle.Top, Height = height };

    private static void StyleButton(Button button, Color back, Color fore)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = back;
        button.ForeColor = fore;
        button.Size = new Size(150, 40);
        button.Margin = new Padding(10, 0, 0, 0);
        button.Font = new Font("Segoe UI Semibold", 10f);
        button.Cursor = Cursors.Hand;
        button.Paint += (_, e) => RoundCorners(button, e);
    }

    /// <summary>Coins arrondis comme les boutons de Windows 11.</summary>
    private static void RoundCorners(Button button, PaintEventArgs e)
    {
        using var path = new GraphicsPath();
        var r = 8;
        var rect = new Rectangle(0, 0, button.Width, button.Height);
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        button.Region = new Region(path);
    }

    private readonly Dictionary<Button, EventHandler> _handlers = [];

    private void SetHandler(Button button, Action action)
    {
        if (_handlers.Remove(button, out var previous))
            button.Click -= previous;
        EventHandler handler = (_, _) => action();
        _handlers[button] = handler;
        button.Click += handler;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
