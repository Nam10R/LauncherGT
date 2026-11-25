using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("GTA VI · Lanzador de emuladores")]
[assembly: AssemblyDescription("Lanzador de XCL y VIctory con seguimiento de CGBJ")]
[assembly: AssemblyProduct("GTA VI · Lanzador")]
[assembly: AssemblyVersion("2.2.1.0")]

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--aplicar-actualizacion")
        {
            try { SelfUpdate.Apply(args); }
            catch (Exception error) { MessageBox.Show(error.Message, "Actualización del launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }
            return;
        }
        SelfUpdate.CleanAfterRestart(args);
        var app = new Application();
        try { app.Run(new LauncherUi(true).Window); }
        catch (Exception error) { MessageBox.Show(error.Message, "GTA VI · Lanzador", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

internal sealed class LauncherUi
{
    internal readonly Window Window;
    private readonly Button refresh;
    private readonly TextBlock footer;
    private readonly List<EngineCard> cards = new List<EngineCard>();
    private readonly EngineCard cgbj;
    private bool checking;
    private bool installing;
    private readonly Button updateLauncher;
    private readonly TextBlock launcherVersion;
    private Version availableVersion;

    private sealed class EngineCard
    {
        internal string Name;
        internal TextBlock Revision, Status;
        internal Button Play, Details, Install, Browse;
        internal UpdateInfo Update;
        internal bool Running;
    }

    private static object ReadLayout(string name)
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            return XamlReader.Load(stream);
    }

    private static SolidColorBrush ColorOf(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    private static BitmapImage ReadImage(string resource, int decodeWidth)
    {
        using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.StreamSource = source;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }

    internal LauncherUi(bool consultOnOpen)
    {
        Window = (Window)ReadLayout("Window.xaml");
        var cover = (Border)Window.FindName("Cover");
        cover.Background = new ImageBrush(ReadImage("Cover.png", 1280)) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
        Window.Width = Math.Min(Window.Width, SystemParameters.WorkArea.Width);
        Window.Height = Math.Min(Window.Height, SystemParameters.WorkArea.Height);
        refresh = (Button)Window.FindName("Refresh");
        footer = (TextBlock)Window.FindName("Footer");
        var discord = (Button)Window.FindName("Discord");
        BindExternalLink(discord, "https://discord.gg/ZFWCgmpNB", "Discord");
        launcherVersion = (TextBlock)Window.FindName("LauncherVersion");
        launcherVersion.Text = "Launcher " + SelfUpdate.Current.ToString(3);
        updateLauncher = (Button)Window.FindName("UpdateLauncher");
        updateLauncher.Click += async delegate { await UpdateLauncher(); };
        ((Button)Window.FindName("ChooseGame")).Click += delegate { ChooseGame(); };
        ShowGameFolder();
        AddCard("XCL", "Xcl.png", "#A8EBDB");
        AddCard("VIctory", "Victory.png", "#FFBBCF");
        cgbj = AddCgbjCard();
        refresh.Click += async delegate { await CheckAll(); };
        if (consultOnOpen)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            timer.Tick += async delegate { await CheckAll(); };
            Window.Loaded += async delegate { timer.Start(); await CheckAll(); };
            Window.Closed += delegate { timer.Stop(); };
        }
        Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!installing) return;
            e.Cancel = true;
            footer.Text = "Esperá a que termine la instalación para cerrar el lanzador.";
        };
    }

    private EngineCard CreateCard(string name, string logoResource, string accent)
    {
        var row = (Border)ReadLayout("Card.xaml");
        ((StackPanel)Window.FindName("Cards")).Children.Add(row);
        ((TextBlock)row.FindName("Engine")).Text = name;
        if (logoResource != null)
        {
            var logo = (Border)row.FindName("Logo");
            logo.Background = new ImageBrush(ReadImage(logoResource, 0)) { Stretch = Stretch.Uniform };
        }
        var card = new EngineCard {
            Name = name, Play = (Button)row.FindName("Play"), Details = (Button)row.FindName("Details"),
            Install = (Button)row.FindName("Install"), Revision = (TextBlock)row.FindName("Base"),
            Status = (TextBlock)row.FindName("Status"), Browse = (Button)row.FindName("Browse")
        };
        card.Play.Style = (Style)Window.FindResource("Action");
        card.Play.Background = ColorOf(accent);
        card.Play.Foreground = ColorOf("#1D1A23");
        card.Play.BorderBrush = ColorOf(accent);
        return card;
    }

    private EngineCard AddCgbjCard()
    {
        var card = CreateCard("CGBJ", null, "#F8CEA2");
        card.Revision.Text = "Versión privada";
        card.Status.Text = "Sin descarga pública";
        card.Browse.Visibility = card.Details.Visibility = card.Install.Visibility = Visibility.Collapsed;
        card.Play.Content = "Ver en PortWatch";
        const string article = "https://portwatch-vi.xyz/news/2026-11-24-cgbj-in-game";
        card.Play.ToolTip = "Abrir el informe de PortWatch sobre CGBJ";
        BindExternalLink(card.Play, article, "PortWatch");
        return card;
    }

    private void BindExternalLink(Button button, string url, string title)
    {
        button.CommandParameter = url;
        button.CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage,
            delegate {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception error) { Alert(error, title); }
            },
            delegate(object sender, CanExecuteRoutedEventArgs e) { e.CanExecute = Object.Equals(e.Parameter, url); }));
        button.Command = NavigationCommands.GoToPage;
    }

    private void AddCard(string name, string logoResource, string accent)
    {
        var card = CreateCard(name, logoResource, accent);
        cards.Add(card);
        card.Play.Content = "▶   Jugar con " + name;
        card.Play.Click += async delegate { await Play(card); };
        card.Browse.Style = card.Details.Style = card.Install.Style = (Style)Window.FindResource("TextAction");
        card.Install.Foreground = ColorOf(accent);
        card.Details.IsEnabled = true;
        card.Details.Click += delegate { ShowDetails(card); };
        card.Install.Click += async delegate { await Install(card); };
        card.Browse.Click += async delegate {
            try
            {
                if (PublicLibrary.SelectExecutable(card.Name) == null) return;
                card.Update = null;
                RefreshCard(card);
                await CheckAll();
            }
            catch (Exception error) { Alert(error, name); }
        };
        RefreshCard(card);
    }

    private void Alert(Exception error, string title)
    {
        MessageBox.Show(Window, error.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string Executable(EngineCard card)
    {
        return PublicLibrary.ExternalExecutable(card.Name) ?? PublicInstall.ExecutablePath(card.Name);
    }

    private void ShowGameFolder()
    {
        var label = (TextBlock)Window.FindName("GameLocation");
        string folder = PublicLibrary.GameFolder;
        label.Text = folder ?? "Elegí dónde tenés tu copia de GTA VI.";
        label.ToolTip = folder;
    }

    private bool ChooseGame()
    {
        try
        {
            if (PublicLibrary.SelectGameFolder() == null) return false;
            ShowGameFolder();
            return true;
        }
        catch (Exception error) { Alert(error, "Carpeta del juego"); return false; }
    }

    private void RefreshCard(EngineCard card)
    {
        string externalPath = PublicLibrary.ExternalExecutable(card.Name);
        bool external = externalPath != null;
        string executable = externalPath ?? PublicInstall.ExecutablePath(card.Name);
        bool ready = File.Exists(executable);
        bool bundled = !external && ready && PublicInstall.IsBundled(card.Name, executable);
        var update = card.Update;
        string local = update == null ? "" : update.LocalSha;
        card.Revision.Text = external ? "Ejecutable elegido" : ready
            ? (!String.IsNullOrEmpty(local) ? "Base  " + local.Substring(0,7) : "Instalado") : "Sin instalar";
        card.Revision.ToolTip = external ? externalPath : bundled
            ? "Versión incluida con el lanzador" : null;
        card.Status.Text = update != null ? update.Status : "Proyecto en GitHub";
        card.Status.ToolTip = update == null ? null : update.Detail;
        bool different = update != null && update.Ahead < 0 && !String.IsNullOrEmpty(update.LocalSha)
            && !String.IsNullOrEmpty(update.RemoteSha) && update.LocalSha != update.RemoteSha;
        bool canDownload = !ready || external || different || (update != null && update.CanUpdate);
        card.Install.Visibility = canDownload ? Visibility.Visible : Visibility.Collapsed;
        card.Install.Content = (!ready || external) ? "Descargar " + card.Name : different ? "Instalar revisión" : "Actualizar " + card.Name;
        card.Install.IsEnabled = !checking && !installing && !card.Running;
        card.Browse.IsEnabled = !checking && !installing && !card.Running;
        card.Play.IsEnabled = ready && !card.Running && !installing;
        card.Play.ToolTip = bundled && card.Name == "XCL"
            ? "Abrir GTA VI con XCL. La primera vez descarga dos bibliotecas de Microsoft."
            : ready ? "Abrir GTA VI con " + card.Name : "Descargá " + card.Name + " o elegí su ejecutable.";
        ToolTipService.SetShowOnDisabled(card.Play, true);
        card.Details.IsEnabled = true;
    }

    private void RefreshCards()
    {
        updateLauncher.IsEnabled = !checking && !installing && !cards.Exists(card => card.Running);
        foreach (var card in cards) RefreshCard(card);
        refresh.IsEnabled = !checking && !installing;
        ((Button)Window.FindName("ChooseGame")).IsEnabled = !installing;
    }

    internal async Task CheckAll()
    {
        if (checking || installing) return;
        checking = true;
        RefreshCards();
        footer.Text = "Consultando GitHub y PortWatch…";
        try
        {
            var tasks = new List<Task> { CheckCgbj(), CheckLauncher() };
            foreach (var card in cards) tasks.Add(Check(card));
            await Task.WhenAll(tasks);
            int online = cards.FindAll(card => card.Update != null && card.Update.Online).Count;
            string github = online == 0 ? "GitHub sin resultados" : String.Format("GitHub respondió para {0} de {1} proyectos", online, cards.Count);
            string portwatch = cgbj.Status.Text == CgbjStatus.Unavailable ? "PortWatch sin resultados" : "PortWatch respondió";
            footer.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + github + " · " + portwatch;
        }
        finally { checking = false; RefreshCards(); }
    }

    private async Task CheckLauncher()
    {
        string label = "Launcher " + SelfUpdate.Current.ToString(3);
        try
        {
            availableVersion = await SelfUpdate.CheckAsync();
            launcherVersion.Text = availableVersion == null ? label + " · Al día" : label + " · Disponible " + availableVersion.ToString(3);
            launcherVersion.ToolTip = null;
            updateLauncher.Visibility = availableVersion == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception error)
        {
            availableVersion = null;
            updateLauncher.Visibility = Visibility.Collapsed;
            launcherVersion.Text = label + " · No se pudo consultar la versión publicada";
            launcherVersion.ToolTip = error.Message;
        }
    }

    private async Task UpdateLauncher()
    {
        if (availableVersion == null || checking || installing || cards.Exists(card => card.Running)) return;
        installing = true;
        RefreshCards();
        try
        {
            footer.Text = "Descargando y verificando Launcher " + availableVersion.ToString(3) + "…";
            await SelfUpdate.PrepareAsync(availableVersion);
            installing = false;
            Window.Close();
        }
        catch (Exception error)
        {
            installing = false;
            RefreshCards();
            footer.Text = "No se pudo actualizar el launcher. Podés volver a intentarlo.";
            Alert(error, "Actualización del launcher");
        }
    }

    private async Task CheckCgbj()
    {
        cgbj.Status.Text = "Consultando PortWatch…";
        cgbj.Status.ToolTip = null;
        try
        {
            string status = await CgbjStatus.CheckAsync();
            cgbj.Status.Text = status;
            if (status != CgbjStatus.Unavailable)
                cgbj.Revision.Text = status == CgbjStatus.RepositoryAnnounced ? "Ficha de PortWatch" : "Versión privada";
            else cgbj.Status.ToolTip = "No se pudo verificar la ficha de CGBJ en PortWatch.";
        }
        catch (Exception error)
        {
            cgbj.Status.Text = CgbjStatus.Unavailable;
            cgbj.Status.ToolTip = error.Message;
        }
    }

    private async Task Check(EngineCard card)
    {
        card.Status.Text = "Consultando GitHub…";
        try
        {
            string local = await Task.Run(() => {
                string external = PublicLibrary.ExternalExecutable(card.Name);
                return external == null ? PublicInstall.InstalledCommit(card.Name) : PublicLibrary.ExternalCommit(external);
            });
            card.Update = await UpdateChecker.CheckAsync(card.Name, local);
        }
        catch (Exception error)
        {
            card.Update = new UpdateInfo { Status = "Consulta no disponible", Detail = error.Message };
        }
    }

    private async Task Install(EngineCard card)
    {
        if (installing || checking || card.Running) return;
        if (!ConfirmInstall(card.Name)) return;
        installing = true;
        RefreshCards();
        string message;
        try
        {
            var progress = new Progress<string>(value => { if (installing) footer.Text = value; });
            footer.Text = "Preparando la descarga de " + card.Name + "…";
            string target = card.Update == null ? null : card.Update.RemoteSha;
            if (String.IsNullOrEmpty(target)) target = await PublicInstall.LatestAsync(card.Name);
            await PublicInstall.InstallAsync(card.Name, target, progress);
            PublicLibrary.ClearExternal(card.Name);
            message = card.Name + " instalado · revisión " + target.Substring(0,7) + ".";
        }
        catch (Exception error)
        {
            message = "No se completó la instalación de " + card.Name + ".";
            Alert(error, message);
        }
        finally { installing = false; RefreshCards(); }
        await CheckAll();
        footer.Text = message;
    }

    private bool ConfirmInstall(string name)
    {
        var dialog = NewDialog("Descargar " + name);
        var panel = DialogPanel(dialog);
        AddLine(panel, "Descargar " + name, 24, true);
        AddLine(panel, "El lanzador descarga el código del proyecto desde GitHub y lo compila en tu PC. La primera instalación puede tardar varios minutos.", 14, false);
        AddLine(panel, "Para compilar", 15, true);
        AddLine(panel, "Necesitás Rust estable para Windows x64 y Visual Studio Build Tools con «Desarrollo para el escritorio con C++», MSVC x64 y Windows SDK.", 14, false);
        var tools = new WrapPanel { Margin = new Thickness(0,0,0,12) };
        panel.Children.Add(tools);
        Link(tools, "Instalar Rust", "https://rustup.rs/");
        Link(tools, "Instalar Build Tools", "https://visualstudio.microsoft.com/visual-cpp-build-tools/");
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        var yes = new Button { Content = "Descargar y compilar", Style = (Style)Window.FindResource("Action"), IsDefault = true };
        yes.Click += delegate { dialog.DialogResult = true; };
        actions.Children.Add(yes);
        var back = new Button { Content = "Volver", Style = (Style)Window.FindResource("Action"), IsCancel = true, Margin = new Thickness(8,0,0,0) };
        actions.Children.Add(back);
        return dialog.ShowDialog() == true;
    }

    private void Link(Panel panel, string label, string url)
    {
        var button = new Button { Content = label, Style = (Style)Window.FindResource("TextAction"), Margin = new Thickness(0,0,18,0) };
        button.Click += delegate {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) { Alert(error, label); }
        };
        panel.Children.Add(button);
    }

    private async Task Play(EngineCard card)
    {
        if (card.Running || installing) return;
        try
        {
            if (PublicLibrary.ValidateGame(PublicLibrary.GameFolder) != null && !ChooseGame()) return;
            string executable = Executable(card);
            installing = true;
            try
            {
                card.Play.Content = "Preparando " + card.Name + "…";
                RefreshCards();
                var progress = new Progress<string>(value => { if (installing) footer.Text = value; });
                await PublicInstall.EnsureBundledDependenciesAsync(card.Name, executable, progress);
            }
            finally
            {
                installing = false;
                card.Play.Content = "▶   Jugar con " + card.Name;
                RefreshCards();
            }
            var process = PublicLibrary.Start(card.Name, executable);
            if (process == null) throw new InvalidOperationException("Windows no devolvió el proceso del emulador.");
            card.Running = true;
            card.Play.IsEnabled = false;
            card.Play.Content = card.Name + " abierto";
            RefreshCards();
            footer.Text = "Se abrió " + card.Name + ".";
            process.Exited += delegate
            {
                int code = process.ExitCode;
                process.Dispose();
                if (Window.Dispatcher.HasShutdownStarted) return;
                Window.Dispatcher.BeginInvoke(new Action(delegate {
                    card.Running = false;
                    card.Play.Content = "▶   Jugar con " + card.Name;
                    RefreshCards();
                    if (!installing) footer.Text = code == 0 ? card.Name + " se cerró." : card.Name + " terminó con el código " + code + ".";
                }));
            };
            process.EnableRaisingEvents = true;
        }
        catch (Exception error)
        {
            footer.Text = "No se pudo abrir " + card.Name + ".";
            Alert(error, "Abrir " + card.Name);
        }
    }

    private Window NewDialog(string title)
    {
        return new Window { Owner = Window, Title = title, Width = Math.Min(525,SystemParameters.WorkArea.Width), SizeToContent = SizeToContent.Height,
            MaxHeight = Math.Min(680,SystemParameters.WorkArea.Height), ResizeMode = ResizeMode.NoResize, Background = ColorOf("#201D28"),
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Foreground = ColorOf("#FFF7F0"), ShowInTaskbar = false };
    }

    private static StackPanel DialogPanel(Window dialog)
    {
        var body = new StackPanel { Margin = new Thickness(26) };
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return body;
    }

    private void ShowDetails(EngineCard card)
    {
        var info = card.Update;
        var dialog = NewDialog("Cambios de " + card.Name);
        var body = DialogPanel(dialog);
        AddLine(body, card.Name, 28, true);
        AddLine(body, card.Status.Text, 17, true);
        string details = info == null ? Convert.ToString(card.Status.ToolTip) : info.Detail;
        if (!String.IsNullOrWhiteSpace(details)) AddLine(body, details, 14, false);
        if (info != null && info.Changes != null && info.Changes.Length > 0)
        {
            AddLine(body, "Cambios recientes", 15, true);
            foreach (var change in info.Changes) AddLine(body, "•  " + change, 13, false);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,15,0,0) };
        body.Children.Add(actions);
        var repo = new Button { Content = "Ver en GitHub", Style = (Style)Window.FindResource("Action") };
        repo.Click += delegate {
            string address = info == null || String.IsNullOrEmpty(info.Url) ? UpdateChecker.ProjectUrl(card.Name) : info.Url;
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Host != "github.com") return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { MessageBox.Show(dialog, error.Message); }
        };
        actions.Children.Add(repo);
        if (card.Install.Visibility != Visibility.Visible)
        {
            var download = new Button { Content = "Descargar " + card.Name, Margin = new Thickness(8,0,0,0),
                Style = (Style)Window.FindResource("Action"), IsEnabled = !installing && !checking && !card.Running };
            download.Click += async delegate { dialog.Close(); await Install(card); };
            actions.Children.Add(download);
        }
        var close = new Button { Content = "Cerrar", IsCancel = true, Margin = new Thickness(8,0,0,0), Style = (Style)Window.FindResource("Action") };
        close.Click += delegate { dialog.Close(); };
        actions.Children.Add(close);
        dialog.ShowDialog();
    }

    private static void AddLine(Panel parent, string value, double size, bool bold)
    {
        parent.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0,0,0,9) });
    }
}
