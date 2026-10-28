using System.Runtime.InteropServices;
using FontsTry;
using Microsoft.Win32;
using NLog.Extensions.Logging;

const string AppName = "FontsTry";
const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string BackgroundSwitch = "--background";
const int SW_HIDE = 0;

// --install   : dodaje program do autostartu bieżącego użytkownika (uruchomi się w tle po zalogowaniu)
// --uninstall : usuwa program z autostartu
// --background: ukrywa okno konsoli (używane przez autostart)
// --events    : które zdarzenia logować, np. KeyDown, KeyUp albo KeyDown,KeyUp (domyślnie All)
// bez argumentów: zwykły program konsolowy, Ctrl+C kończy
if (args.Contains("--install"))
{
    using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
    // Pozostałe argumenty (np. --events KeyDown) przechodzą do autostartu
    var passThroughArgs = args.Where(a => a != "--install").Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
    runKey.SetValue(AppName, string.Join(' ', [$"\"{Environment.ProcessPath}\"", BackgroundSwitch, .. passThroughArgs]));
    Console.WriteLine($"Dodano do autostartu: {Environment.ProcessPath}");
    return;
}

if (args.Contains("--uninstall"))
{
    using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
    runKey?.DeleteValue(AppName, throwOnMissingValue: false);
    Console.WriteLine("Usunięto z autostartu.");
    return;
}

// Blokada pojedynczej instancji w sesji użytkownika — inaczej każdy klawisz byłby logowany wielokrotnie
using var instanceMutex = new Mutex(initiallyOwned: true, $@"Local\{AppName}", out bool isFirstInstance);
if (!isFirstInstance)
{
    Console.WriteLine($"{AppName} już działa w tej sesji. Zakończ uruchomioną instancję przed startem nowej.");
    return;
}

if (args.Contains(BackgroundSwitch))
{
    ShowWindow(GetConsoleWindow(), SW_HIDE);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => a != BackgroundSwitch).ToArray(),
    // Z autostartu bieżący katalog to System32, więc konfigurację czytamy z katalogu programu
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddHostedService<Worker>();

builder.Logging.ClearProviders();
builder.Logging.AddNLog();

var host = builder.Build();
host.Run();

[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
